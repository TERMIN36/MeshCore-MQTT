using System.Text;
using MeshCoreMqtt.Core;

namespace MeshCoreMqtt.Core.Tests;

public class TunnelFramesTests
{
    static readonly Guid Mine = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    static readonly Guid Other = Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff");

    [Fact]
    public void Publish_stays_inside_the_tunnel_and_comes_back_as_pubkey()
    {
        var prefix = MeshTopics.Prefix(Mine);
        var packet = Publish("pk-1", "hello");
        var status = TunnelFrames.TryRewrite(packet, true, prefix, 4, out var stored, out var consumed);
        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(packet.Length, consumed);

        var topic = TopicOf(stored);
        Assert.Equal(prefix + "/pk-1", topic);
        Assert.True(TopicRules.AllowsPublish([MeshTopics.Filter(Mine)], topic));
        Assert.False(TopicRules.AllowsPublish([MeshTopics.Filter(Other)], topic));
        Assert.False(TopicRules.AllowsSubscribe([MeshTopics.Filter(Other)], topic));

        status = TunnelFrames.TryRewrite(stored, false, prefix, 4, out var shown, out _);
        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal("pk-1", TopicOf(shown));
        Assert.Equal("hello", Encoding.UTF8.GetString(PayloadOf(shown)));
    }

    [Fact]
    public void Subscribe_hash_is_limited_to_this_tunnel()
    {
        var prefix = MeshTopics.Prefix(Mine);
        var packet = Subscribe("#");
        var status = TunnelFrames.TryRewrite(packet, true, prefix, 4, out var stored, out _);
        Assert.Equal(FrameStatus.Ok, status);
        var filter = FilterOf(stored);
        Assert.Equal(prefix + "/#", filter);
        Assert.True(TopicRules.AllowsSubscribe([MeshTopics.Filter(Mine)], filter));
        Assert.False(TopicRules.AllowsSubscribe([MeshTopics.Filter(Mine)], "#"));
        Assert.False(TopicRules.AllowsSubscribe([MeshTopics.Filter(Other)], filter));
    }

    [Fact]
    public void Foreign_publish_is_not_forwarded_to_the_repeater()
    {
        var foreign = Publish(MeshTopics.Prefix(Other) + "/pk-2", "x");
        var status = TunnelFrames.TryRewrite(foreign, false, MeshTopics.Prefix(Mine), 4, out var output, out var consumed);
        Assert.Equal(FrameStatus.Ok, status);
        Assert.Empty(output);
        Assert.Equal(foreign.Length, consumed);
    }

    [Fact]
    public void Will_topic_gets_the_tunnel_prefix()
    {
        var prefix = MeshTopics.Prefix(Mine);
        var packet = ConnectWithWill("user-1", "pk-will");
        var status = TunnelFrames.TryRewrite(packet, true, prefix, 4, out var stored, out _);
        Assert.Equal(FrameStatus.Ok, status);
        Assert.True(ConnectReader.TryReadUsername(stored, out var username, out var length, out var needMore));
        Assert.False(needMore);
        Assert.Equal(stored.Length, length);
        Assert.Equal("user-1", username);
        Assert.Contains(prefix + "/pk-will", Encoding.UTF8.GetString(stored), StringComparison.Ordinal);
        Assert.DoesNotContain("\0pk-will", Encoding.UTF8.GetString(stored), StringComparison.Ordinal);
    }

    [Fact]
    public void Publish_of_a_bridge_frame_exposes_the_repeater_key()
    {
        var frame = new List<byte> { 1, 2 };
        frame.AddRange(Enumerable.Repeat((byte)0xAB, 32));
        frame.Add(0);
        frame.AddRange(new byte[4]);
        frame.Add(0);
        frame.Add(0);
        frame.Add(0);
        var expected = string.Concat(Enumerable.Repeat("ab", 32));

        Assert.True(TunnelFrames.TryPublisherKey(Publish("out", frame.ToArray()), 4, out var found));
        Assert.Equal(expected, found);

        var qos1 = new MemoryStream();
        WriteString(qos1, "out");
        qos1.WriteByte(0);
        qos1.WriteByte(7);
        qos1.Write(frame.ToArray());
        Assert.True(TunnelFrames.TryPublisherKey(Pack(0x32, qos1.ToArray()), 4, out var withId));
        Assert.Equal(expected, withId);

        var mqtt5 = new MemoryStream();
        WriteString(mqtt5, "out");
        mqtt5.WriteByte(0);
        mqtt5.Write(frame.ToArray());
        Assert.True(TunnelFrames.TryPublisherKey(Pack(0x30, mqtt5.ToArray()), 5, out var version5));
        Assert.Equal(expected, version5);

        Assert.False(TunnelFrames.TryPublisherKey(Publish("out", "hello"), 4, out _));
        Assert.False(TunnelFrames.TryPublisherKey(Subscribe("#"), 4, out _));
    }

    [Fact]
    public void Waits_for_the_rest_of_a_split_packet()
    {
        var packet = Publish("pk", "body");
        var status = TunnelFrames.TryRewrite(packet.AsSpan(0, 3), true, MeshTopics.Prefix(Mine), 4, out _, out var consumed);
        Assert.Equal(FrameStatus.NeedMore, status);
        Assert.Equal(0, consumed);
    }

    static byte[] ConnectWithWill(string username, string willTopic)
    {
        var body = new MemoryStream();
        WriteString(body, "MQTT");
        body.WriteByte(4);
        body.WriteByte(0x84);
        body.WriteByte(0);
        body.WriteByte(60);
        WriteString(body, "client");
        WriteString(body, willTopic);
        WriteString(body, "offline");
        WriteString(body, username);
        return Pack(0x10, body.ToArray());
    }

    static byte[] Publish(string topic, string payload) => Publish(topic, Encoding.UTF8.GetBytes(payload));

    static byte[] Publish(string topic, byte[] payload)
    {
        var body = new MemoryStream();
        WriteString(body, topic);
        body.Write(payload);
        return Pack(0x30, body.ToArray());
    }

    static byte[] Subscribe(string filter)
    {
        var body = new MemoryStream();
        body.WriteByte(0);
        body.WriteByte(1);
        WriteString(body, filter);
        body.WriteByte(0);
        return Pack(0x82, body.ToArray());
    }

    static string FilterOf(byte[] packet)
    {
        var body = Body(packet);
        var length = (body[2] << 8) | body[3];
        return Encoding.UTF8.GetString(body, 4, length);
    }

    static string TopicOf(byte[] packet)
    {
        var body = Body(packet);
        var length = (body[0] << 8) | body[1];
        return Encoding.UTF8.GetString(body, 2, length);
    }

    static byte[] PayloadOf(byte[] packet)
    {
        var body = Body(packet);
        var length = (body[0] << 8) | body[1];
        return body[(2 + length)..];
    }

    static byte[] Body(byte[] packet)
    {
        var bytes = 0;
        var value = 0;
        var multiplier = 1;
        for (var i = 1; i < packet.Length; i++)
        {
            value += (packet[i] & 0x7F) * multiplier;
            bytes++;
            if ((packet[i] & 0x80) == 0)
                break;
            multiplier *= 128;
        }

        return packet[(1 + bytes)..(1 + bytes + value)];
    }

    static byte[] Pack(byte header, byte[] body)
    {
        var packet = new MemoryStream();
        packet.WriteByte(header);
        var length = body.Length;
        do
        {
            var encoded = length % 128;
            length /= 128;
            if (length > 0)
                encoded |= 0x80;
            packet.WriteByte((byte)encoded);
        } while (length > 0);
        packet.Write(body);
        return packet.ToArray();
    }

    static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.WriteByte((byte)(bytes.Length >> 8));
        stream.WriteByte((byte)bytes.Length);
        stream.Write(bytes);
    }
}
