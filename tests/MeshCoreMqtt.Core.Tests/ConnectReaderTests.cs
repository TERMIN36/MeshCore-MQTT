using System.Text;
using MeshCoreMqtt.Core;

namespace MeshCoreMqtt.Core.Tests;

public class ConnectReaderTests
{
    [Fact]
    public void Reads_username_from_mqtt311_connect()
    {
        var packet = BuildConnect(4, "client-1", "k7Qm2pLx9nRw4sTd", "7fK2-mQ9p-Lx4n");
        var ok = ConnectReader.TryReadUsername(packet, out var username, out var length, out var needMore);
        Assert.True(ok);
        Assert.False(needMore);
        Assert.Equal(packet.Length, length);
        Assert.Equal("k7Qm2pLx9nRw4sTd", username);
    }

    [Fact]
    public void Reads_username_from_mqtt5_connect()
    {
        var packet = BuildConnect(5, "client-1", "user-name", "secret");
        var ok = ConnectReader.TryReadUsername(packet, out var username, out _, out var needMore);
        Assert.True(ok);
        Assert.False(needMore);
        Assert.Equal("user-name", username);
    }

    [Fact]
    public void Asks_for_more_bytes_until_the_packet_is_complete()
    {
        var packet = BuildConnect(4, "c", "user", "pass");
        var partial = packet.AsSpan(0, 4);
        var ok = ConnectReader.TryReadUsername(partial, out _, out _, out var needMore);
        Assert.False(ok);
        Assert.True(needMore);
    }

    static byte[] BuildConnect(byte level, string clientId, string username, string password)
    {
        var payload = new MemoryStream();
        WriteString(payload, "MQTT");
        payload.WriteByte(level);
        payload.WriteByte(0xC0);
        payload.WriteByte(0);
        payload.WriteByte(60);
        if (level == 5)
            payload.WriteByte(0);
        WriteString(payload, clientId);
        WriteString(payload, username);
        WriteString(payload, password);

        var body = payload.ToArray();
        var packet = new MemoryStream();
        packet.WriteByte(0x10);
        WriteRemaining(packet, body.Length);
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

    static void WriteRemaining(Stream stream, int length)
    {
        do
        {
            var encoded = length % 128;
            length /= 128;
            if (length > 0)
                encoded |= 0x80;
            stream.WriteByte((byte)encoded);
        } while (length > 0);
    }
}
