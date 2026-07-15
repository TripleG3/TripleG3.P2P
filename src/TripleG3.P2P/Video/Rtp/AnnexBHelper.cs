namespace TripleG3.P2P.Video.Rtp;

internal static class AnnexBHelper
{
    public static List<ReadOnlyMemory<byte>> EnumerateNalUnits(ReadOnlyMemory<byte> memory)
    {
        var units = new List<ReadOnlyMemory<byte>>();
        var data = memory.Span;
        var searchIndex = 0;
        while (TryFindStartCode(data, searchIndex, out var startCodeIndex, out var startCodeLength))
        {
            var nalStart = startCodeIndex + startCodeLength;
            if (TryFindStartCode(data, nalStart, out var nextStartCodeIndex, out _))
            {
                if (nextStartCodeIndex > nalStart)
                {
                    units.Add(memory.Slice(nalStart, nextStartCodeIndex - nalStart));
                }

                searchIndex = nextStartCodeIndex;
                continue;
            }

            if (nalStart < data.Length)
            {
                units.Add(memory[nalStart..]);
            }

            break;
        }

        return units;
    }

    private static bool TryFindStartCode(
        ReadOnlySpan<byte> data,
        int startIndex,
        out int startCodeIndex,
        out int startCodeLength)
    {
        for (var index = startIndex; index + 2 < data.Length; index++)
        {
            if (data[index] != 0 || data[index + 1] != 0) continue;
            if (data[index + 2] == 1)
            {
                startCodeIndex = index;
                startCodeLength = 3;
                return true;
            }

            if (index + 3 < data.Length && data[index + 2] == 0 && data[index + 3] == 1)
            {
                startCodeIndex = index;
                startCodeLength = 4;
                return true;
            }
        }

        startCodeIndex = -1;
        startCodeLength = 0;
        return false;
    }
}