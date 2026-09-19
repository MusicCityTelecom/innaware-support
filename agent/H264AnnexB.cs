namespace InnAwareSupport.Agent;

internal static class H264AnnexB
{
    public static byte[] Normalize(byte[] data)
    {
        if (data.Length < 4 || HasStartCode(data, 0))
            return data;

        using var output = new MemoryStream(data.Length + 64);
        var offset = 0;

        while (offset + 4 <= data.Length)
        {
            var length =
                (data[offset] << 24) |
                (data[offset + 1] << 16) |
                (data[offset + 2] << 8) |
                data[offset + 3];

            offset += 4;

            if (length <= 0 || offset + length > data.Length)
                return data;

            output.Write([0, 0, 0, 1]);
            output.Write(data, offset, length);
            offset += length;
        }

        return offset == data.Length ? output.ToArray() : data;
    }

    public static bool ContainsNalType(ReadOnlySpan<byte> data, int wantedType)
    {
        var i = 0;
        while (i + 3 < data.Length)
        {
            var startLength = StartCodeLength(data, i);
            if (startLength == 0)
            {
                i++;
                continue;
            }

            var nalIndex = i + startLength;
            if (nalIndex < data.Length && (data[nalIndex] & 0x1F) == wantedType)
                return true;

            i = nalIndex + 1;
        }

        return false;
    }

    private static bool HasStartCode(ReadOnlySpan<byte> data, int offset) =>
        StartCodeLength(data, offset) != 0;

    private static int StartCodeLength(ReadOnlySpan<byte> data, int offset)
    {
        if (offset + 3 < data.Length &&
            data[offset] == 0 &&
            data[offset + 1] == 0 &&
            data[offset + 2] == 0 &&
            data[offset + 3] == 1)
            return 4;

        if (offset + 2 < data.Length &&
            data[offset] == 0 &&
            data[offset + 1] == 0 &&
            data[offset + 2] == 1)
            return 3;

        return 0;
    }
}
