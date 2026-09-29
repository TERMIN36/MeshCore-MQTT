using System.Text;

namespace MeshCoreMqtt.Core;

public enum FrameStatus
{
    NeedMore,
    Ok,
    Invalid
}

public static class TunnelFrames
{
    public static FrameStatus TryRewrite(
        ReadOnlySpan<byte> data,
        bool toBroker,
        string prefix,
        byte protocolLevel,
        out byte[] output,
        out int consumed)
    {
        output = [];
        consumed = 0;
        if (string.IsNullOrEmpty(prefix) || prefix.Contains('+') || prefix.Contains('#'))
            return FrameStatus.Invalid;
        if (data.Length < 2)
            return FrameStatus.NeedMore;

        var lengthStatus = TryRemainingLength(data[1..], out var remaining, out var lengthBytes);
        if (lengthStatus == LengthStatus.NeedMore)
            return FrameStatus.NeedMore;
        if (lengthStatus == LengthStatus.Invalid || remaining > ConnectReader.MaxPacketBytes)
            return FrameStatus.Invalid;

        var packetLength = 1 + lengthBytes + remaining;
        if (data.Length < packetLength)
            return FrameStatus.NeedMore;

        consumed = packetLength;
        var header = data[0];
        var type = header >> 4;
        var body = data.Slice(1 + lengthBytes, remaining);
        if (type == 1 && toBroker)
            return RewriteConnect(header, body, prefix, out output);
        if (type == 3)
            return RewritePublish(header, body, toBroker, prefix, out output);
        if (toBroker && type is 8 or 10)
            return RewriteFilters(header, body, type == 8, protocolLevel, prefix, out output);

        output = data[..packetLength].ToArray();
        return FrameStatus.Ok;
    }

    static FrameStatus RewriteConnect(byte header, ReadOnlySpan<byte> body, string prefix, out byte[] output)
    {
        output = [];
        var offset = 0;
        if (!TryBytes(body, ref offset, out _) || offset >= body.Length)
            return FrameStatus.Invalid;
        var level = body[offset++];
        if (offset >= body.Length)
            return FrameStatus.Invalid;
        var flags = body[offset++];
        if (offset + 2 > body.Length)
            return FrameStatus.Invalid;
        offset += 2;
        if (level == 5 && !TrySkipProperties(body, ref offset))
            return FrameStatus.Invalid;
        if (!TryBytes(body, ref offset, out _))
            return FrameStatus.Invalid;
        if ((flags & 0x04) == 0)
        {
            output = Pack(header, body);
            return FrameStatus.Ok;
        }

        if (level == 5 && !TrySkipProperties(body, ref offset))
            return FrameStatus.Invalid;
        if (!TryBytes(body, ref offset, out var willTopic))
            return FrameStatus.Invalid;
        var next = AddPrefix(willTopic, prefix);
        if (next.Length > ushort.MaxValue)
            return FrameStatus.Invalid;

        var rebuilt = new byte[offset - willTopic.Length + next.Length + (body.Length - offset)];
        var topicAt = offset - (2 + willTopic.Length);
        body[..topicAt].CopyTo(rebuilt);
        rebuilt[topicAt] = (byte)(next.Length >> 8);
        rebuilt[topicAt + 1] = (byte)next.Length;
        next.CopyTo(rebuilt.AsSpan(topicAt + 2));
        body[offset..].CopyTo(rebuilt.AsSpan(topicAt + 2 + next.Length));
        output = Pack(header, rebuilt);
        return FrameStatus.Ok;
    }

    static FrameStatus RewritePublish(byte header, ReadOnlySpan<byte> body, bool toBroker, string prefix, out byte[] output)
    {
        output = [];
        var offset = 0;
        if (!TryBytes(body, ref offset, out var topic))
            return FrameStatus.Invalid;

        byte[] next;
        if (toBroker)
            next = AddPrefix(topic, prefix);
        else if (!TryStrip(topic, prefix, out next))
            return FrameStatus.Ok;

        if (next.Length > ushort.MaxValue)
            return FrameStatus.Invalid;

        var rebuilt = new byte[2 + next.Length + (body.Length - offset)];
        rebuilt[0] = (byte)(next.Length >> 8);
        rebuilt[1] = (byte)next.Length;
        next.CopyTo(rebuilt.AsSpan(2));
        body[offset..].CopyTo(rebuilt.AsSpan(2 + next.Length));
        output = Pack(header, rebuilt);
        return FrameStatus.Ok;
    }

    static FrameStatus RewriteFilters(byte header, ReadOnlySpan<byte> body, bool withQos, byte protocolLevel, string prefix, out byte[] output)
    {
        output = [];
        if (body.Length < 2)
            return FrameStatus.Invalid;

        var offset = 2;
        if (protocolLevel == 5 && !TrySkipProperties(body, ref offset))
            return FrameStatus.Invalid;

        var rebuilt = new MemoryStream();
        rebuilt.Write(body[..2]);
        if (protocolLevel == 5)
            rebuilt.Write(body[2..offset]);

        while (offset < body.Length)
        {
            if (!TryBytes(body, ref offset, out var filter))
                return FrameStatus.Invalid;
            var next = AddPrefix(filter, prefix);
            if (next.Length > ushort.MaxValue)
                return FrameStatus.Invalid;
            rebuilt.WriteByte((byte)(next.Length >> 8));
            rebuilt.WriteByte((byte)next.Length);
            rebuilt.Write(next);
            if (!withQos)
                continue;
            if (offset >= body.Length)
                return FrameStatus.Invalid;
            rebuilt.WriteByte(body[offset++]);
        }

        output = Pack(header, rebuilt.ToArray());
        return FrameStatus.Ok;
    }

    static byte[] AddPrefix(ReadOnlySpan<byte> topic, string prefix)
    {
        var head = Encoding.UTF8.GetBytes(prefix);
        var next = new byte[head.Length + 1 + topic.Length];
        head.CopyTo(next, 0);
        next[head.Length] = (byte)'/';
        topic.CopyTo(next.AsSpan(head.Length + 1));
        return next;
    }

    static bool TryStrip(ReadOnlySpan<byte> topic, string prefix, out byte[] stripped)
    {
        var head = Encoding.UTF8.GetBytes(prefix + "/");
        if (!topic.StartsWith(head))
        {
            stripped = [];
            return false;
        }

        stripped = topic[head.Length..].ToArray();
        return true;
    }

    static byte[] Pack(byte header, ReadOnlySpan<byte> body)
    {
        var length = EncodeRemaining(body.Length);
        var packet = new byte[1 + length.Length + body.Length];
        packet[0] = header;
        length.CopyTo(packet, 1);
        body.CopyTo(packet.AsSpan(1 + length.Length));
        return packet;
    }

    static byte[] EncodeRemaining(int length)
    {
        var bytes = new byte[4];
        var count = 0;
        do
        {
            var encoded = length % 128;
            length /= 128;
            if (length > 0)
                encoded |= 0x80;
            bytes[count++] = (byte)encoded;
        } while (length > 0);

        return bytes[..count];
    }

    static LengthStatus TryRemainingLength(ReadOnlySpan<byte> data, out int value, out int bytes)
    {
        value = 0;
        bytes = 0;
        var multiplier = 1;
        for (var i = 0; i < 4; i++)
        {
            if (i >= data.Length)
                return LengthStatus.NeedMore;
            var encoded = data[i];
            value += (encoded & 0x7F) * multiplier;
            bytes++;
            if ((encoded & 0x80) == 0)
                return LengthStatus.Ok;
            multiplier *= 128;
            if (multiplier > 128 * 128 * 128)
                return LengthStatus.Invalid;
        }

        return LengthStatus.Invalid;
    }

    static bool TryBytes(ReadOnlySpan<byte> data, ref int offset, out byte[] bytes)
    {
        bytes = [];
        if (offset + 2 > data.Length)
            return false;
        var length = (data[offset] << 8) | data[offset + 1];
        offset += 2;
        if (offset + length > data.Length)
            return false;
        bytes = data.Slice(offset, length).ToArray();
        offset += length;
        return true;
    }

    static bool TrySkipProperties(ReadOnlySpan<byte> data, ref int offset)
    {
        if (!TryVariableInt(data, ref offset, out var length))
            return false;
        if (offset + length > data.Length)
            return false;
        offset += length;
        return true;
    }

    static bool TryVariableInt(ReadOnlySpan<byte> data, ref int offset, out int value)
    {
        value = 0;
        var multiplier = 1;
        for (var i = 0; i < 4; i++)
        {
            if (offset >= data.Length)
                return false;
            var encoded = data[offset++];
            value += (encoded & 0x7F) * multiplier;
            if ((encoded & 0x80) == 0)
                return true;
            multiplier *= 128;
        }

        return false;
    }

    enum LengthStatus
    {
        Ok,
        NeedMore,
        Invalid
    }
}
