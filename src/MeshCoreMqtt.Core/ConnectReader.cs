using System.Text;

namespace MeshCoreMqtt.Core;

public static class ConnectReader
{
    public const int MaxPacketBytes = 262144;

    public static bool TryReadUsername(ReadOnlySpan<byte> data, out string? username, out int packetLength, out bool needMore) =>
        TryReadUsername(data, out username, out packetLength, out needMore, out _);

    public static bool TryReadUsername(ReadOnlySpan<byte> data, out string? username, out int packetLength, out bool needMore, out byte protocolLevel)
    {
        username = null;
        packetLength = 0;
        needMore = false;
        protocolLevel = 0;
        if (data.Length < 2)
        {
            needMore = true;
            return false;
        }

        if ((data[0] >> 4) != 1)
            return false;

        var lengthStatus = TryRemainingLength(data[1..], out var remaining, out var lengthBytes);
        if (lengthStatus == LengthStatus.NeedMore)
        {
            needMore = true;
            return false;
        }

        if (lengthStatus == LengthStatus.Invalid || remaining > MaxPacketBytes)
            return false;

        packetLength = 1 + lengthBytes + remaining;
        if (data.Length < packetLength)
        {
            needMore = true;
            return false;
        }

        var span = data.Slice(1 + lengthBytes, remaining);
        var offset = 0;
        if (!TryString(span, ref offset, out _) || offset >= span.Length)
            return false;

        var level = span[offset++];
        protocolLevel = level;
        if (offset >= span.Length)
            return false;
        var flags = span[offset++];
        if (offset + 2 > span.Length)
            return false;
        offset += 2;

        if (level == 5 && !TrySkipProperties(span, ref offset))
            return false;
        if (!TryString(span, ref offset, out _))
            return false;

        var hasUser = (flags & 0x80) != 0;
        var hasWill = (flags & 0x04) != 0;
        if (hasWill)
        {
            if (level == 5 && !TrySkipProperties(span, ref offset))
                return false;
            if (!TryString(span, ref offset, out _) || !TrySkipBinary(span, ref offset))
                return false;
        }

        if (!hasUser)
            return true;
        return TryString(span, ref offset, out username) && !string.IsNullOrEmpty(username);
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
        }

        return LengthStatus.Invalid;
    }

    static bool TryString(ReadOnlySpan<byte> data, ref int offset, out string value)
    {
        value = "";
        if (!TryBinary(data, ref offset, out var bytes))
            return false;
        value = Encoding.UTF8.GetString(bytes);
        return true;
    }

    static bool TrySkipBinary(ReadOnlySpan<byte> data, ref int offset) =>
        TryBinary(data, ref offset, out _);

    static bool TryBinary(ReadOnlySpan<byte> data, ref int offset, out byte[] bytes)
    {
        bytes = [];
        if (offset + 2 > data.Length)
            return false;
        var length = (data[offset] << 8) | data[offset + 1];
        offset += 2;
        if (length < 0 || offset + length > data.Length)
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
