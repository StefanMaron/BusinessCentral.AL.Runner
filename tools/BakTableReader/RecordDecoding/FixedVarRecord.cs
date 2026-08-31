// FixedVarRecord — the uncompressed "primary record" row format (status byte
// 0x10/0x20/0x30 in the AL Runner #2241 measurement). Ported from OrcaMDF's
// Record.cs / PrimaryRecord.cs (github.com/improvedk/OrcaMDF, MIT-licensed).
//
// Only the shapes this spike actually needed are implemented: Primary rows
// with a null bitmap and/or variable-length columns. Ghost records, forwarded
// records and forwarding stubs are recognised (via RecordType) but not
// resolved -- a caller asking for their data gets NotSupportedException
// rather than silently wrong bytes.
namespace BakTableReader.RecordDecoding;

public enum RecordType : byte
{
    Primary = 0,
    Forwarded = 1,
    ForwardingStub = 2,
    Index = 3,
    BlobFragment = 4,
    GhostIndex = 5,
    GhostData = 6,
    GhostVersion = 7,
}

public sealed class FixedVarRecord
{
    public RecordType Type { get; }
    public bool HasNullBitmap { get; }
    public bool HasVariableLengthColumns { get; }
    public byte[] FixedLengthData { get; }
    public short NumberOfColumns { get; }
    public byte[]? NullBitmap { get; }
    public IReadOnlyList<(bool Complex, byte[] Data)> VariableLengthColumns { get; }
    /// <summary>Total bytes this record occupies, relative to its start offset.</summary>
    public int Length { get; }

    private FixedVarRecord(RecordType type, bool hasNullBitmap, bool hasVarLen,
        byte[] fixedData, short numberOfColumns, byte[]? nullBitmap,
        IReadOnlyList<(bool, byte[])> varLenColumns, int length)
    {
        Type = type;
        HasNullBitmap = hasNullBitmap;
        HasVariableLengthColumns = hasVarLen;
        FixedLengthData = fixedData;
        NumberOfColumns = numberOfColumns;
        NullBitmap = nullBitmap;
        VariableLengthColumns = varLenColumns;
        Length = length;
    }

    public bool IsNull(int zeroBasedColumnIndex)
    {
        if (NullBitmap is null)
            return false;
        int byteIndex = zeroBasedColumnIndex / 8;
        int bitIndex = zeroBasedColumnIndex % 8;
        if (byteIndex >= NullBitmap.Length)
            return false;
        return (NullBitmap[byteIndex] & (1 << bitIndex)) != 0;
    }

    /// <summary>Parses a record starting at <paramref name="start"/> within
    /// <paramref name="page"/> (a full 8192-byte page buffer).</summary>
    public static FixedVarRecord Parse(byte[] page, int start)
    {
        byte statusA = page[start];
        var type = (RecordType)((statusA >> 1) & 0x7);
        bool hasNullBitmap = (statusA & 0x10) != 0;
        bool hasVarLen = (statusA & 0x20) != 0;

        if (type is RecordType.ForwardingStub or RecordType.Forwarded)
            throw new NotSupportedException($"{type} records are not resolved by this reader");

        int p = start + 2; // status bits A + B
        short fixedLength = (short)(BitConverter.ToInt16(page, p) - 4);
        p += 2;
        var fixedData = page.AsSpan(p, fixedLength).ToArray();
        p += fixedLength;

        short numberOfColumns = BitConverter.ToInt16(page, p);
        p += 2;

        byte[]? nullBitmap = null;
        if (hasNullBitmap)
        {
            int nBytes = (numberOfColumns + 7) / 8;
            nullBitmap = page.AsSpan(p, nBytes).ToArray();
            p += nBytes;
        }

        var varLenColumns = new List<(bool, byte[])>();
        if (hasVarLen)
        {
            short numVarLen = BitConverter.ToInt16(page, p);
            p += 2;
            var endOffsets = new short[numVarLen];
            for (int i = 0; i < numVarLen; i++)
            {
                endOffsets[i] = BitConverter.ToInt16(page, p);
                p += 2;
            }

            int prevEnd = p;
            foreach (short rawEnd in endOffsets)
            {
                bool complex = (rawEnd & unchecked((short)0x8000)) != 0;
                // Column-offset-array entries are record-relative cumulative END
                // offsets (per OrcaMDF Record.cs), not lengths, and not
                // buffer-absolute -- they must be added to `start`.
                int end = start + (rawEnd & 0x7FFF);
                varLenColumns.Add((complex, page.AsSpan(prevEnd, end - prevEnd).ToArray()));
                prevEnd = end;
            }
            p = prevEnd;
        }

        return new FixedVarRecord(type, hasNullBitmap, hasVarLen, fixedData,
            numberOfColumns, nullBitmap, varLenColumns, p - start);
    }
}
