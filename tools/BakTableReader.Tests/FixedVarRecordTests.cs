using System.Text;
using BakTableReader.RecordDecoding;
using Xunit;

namespace BakTableReader.Tests;

public class FixedVarRecordTests
{
    // Captured verbatim from the real BC 28.1 W1 sandbox demo .bak (AL Runner
    // #2241) -- a sysallocunits row for the fixed sysrowsets allocation unit
    // (auid 327680). No null values, no variable-length columns.
    private const string SysAllocUnitRowHex =
        "100045000000050000000000010000050000000000000000000100110000000100a000000001008300000001006800000000000000660000000000000075000000000000000b0000f8";

    // A sysschobjs row for object id 34 ("sysschobjs" itself, a SYSTEM_TABLE) --
    // has a null bitmap and one variable-length column (the sysname `name`).
    private const string SysSchObjRowHex =
        "3000300022000000040000000001000e00532000000000010c0000008702d600ea9b000004c36a0028af0000000000000c0000f001004c007300790073007300630068006f0062006a007300";

    private static FixedVarRecord ParseHex(string hex)
    {
        var page = new byte[PageHeader.PageLength];
        Convert.FromHexString(hex).CopyTo(page, 0);
        return FixedVarRecord.Parse(page, 0);
    }

    [Fact]
    public void Parse_SysAllocUnitRow_HasNoVarLenColumns()
    {
        var record = ParseHex(SysAllocUnitRowHex);

        Assert.Equal(RecordType.Primary, record.Type);
        Assert.True(record.HasNullBitmap);
        Assert.False(record.HasVariableLengthColumns);
        Assert.Equal(11, record.NumberOfColumns);
        Assert.Equal(65, record.FixedLengthData.Length);

        long auid = BitConverter.ToInt64(record.FixedLengthData, 0);
        byte type = record.FixedLengthData[8];
        Assert.Equal(327680, auid);
        Assert.Equal(1, type); // IN_ROW_DATA
    }

    [Fact]
    public void Parse_SysSchObjRow_DecodesIdNameAndType()
    {
        var record = ParseHex(SysSchObjRowHex);

        Assert.True(record.HasNullBitmap);
        Assert.True(record.HasVariableLengthColumns);
        Assert.Equal(12, record.NumberOfColumns);
        Assert.Single(record.VariableLengthColumns);

        int id = BitConverter.ToInt32(record.FixedLengthData, 0);
        string type = Encoding.ASCII.GetString(record.FixedLengthData, 13, 2);
        string name = Encoding.Unicode.GetString(record.VariableLengthColumns[0].Data);

        Assert.Equal(34, id);
        Assert.Equal("S ", type); // SYSTEM_TABLE
        Assert.Equal("sysschobjs", name);
        Assert.False(record.VariableLengthColumns[0].Complex);
    }

    [Fact]
    public void IsNull_ReturnsFalseWhenNoNullBitmapPresent()
    {
        // Fabricate a minimal record with the null-bitmap bit clear.
        var page = new byte[PageHeader.PageLength];
        page[0] = 0x00; // Primary, no null bitmap, no varlen
        page[1] = 0x00;
        BitConverter.GetBytes((short)4).CopyTo(page, 2); // fixedLength = 0
        BitConverter.GetBytes((short)0).CopyTo(page, 4); // numberOfColumns = 0

        var record = FixedVarRecord.Parse(page, 0);

        Assert.False(record.HasNullBitmap);
        Assert.False(record.IsNull(0));
    }
}
