using System.Text;
using BakTableReader.RecordDecoding;
using Xunit;

namespace BakTableReader.Tests;

public class CompressedRecordTests
{
    // Captured verbatim from the real BC 28.1 W1 sandbox demo .bak (AL Runner
    // #2241). This is the base "CRONUS International Ltd_ Code
    // Setup$<guid>" row -- 7 native SQL columns (a versioning/partition
    // column, the primary key, and 5 platform system columns: SystemId,
    // SystemCreatedAt/By, SystemModifiedAt/By). PAGE-compressed
    // (sysrowsets.cmprlevel == 2).
    private const string BaseRowHex =
        "2107418a8a1a03fb8380b44f0181b6a880b44f0181b6a8010300100020003000dbd1420053d911f18e267ced8d9e40940000000000000000000000000000000100000000000000000000000000000001";

    // The companion "..." table for the same row -- 84 columns, one per
    // AL business field (Sales, Purchases, Inventory Post Cost, ...). This is
    // the row that reproduces the cluster-pointer bug described in the file
    // header of CompressedRecord.cs: 84 columns need 3 short-data clusters.
    private const string ExtRowHex =
        "215441a6aa11aaa8aa8a88aa88aa1aaaaaaa8a8a8aaaaaaaaaa8aa8486aaaaaaaa6a88a8aaaaaaaaa8aaa88a323203fb8453414c455347454e4a4e4c104954454d4a4e4c5245534a4e4c1050524f4a4a4e4c56415453544d54434f4d5052474c46494e4348524744454c4554451042414e4b5245434346574b534810474c1043414a4f555210414c4c4f435452414255441046414a4e4c4641474c4a4e4c494e534a4e4c10434f4d5052464157484954454d1057485049434b1053455256494345013900090012001d0026002f00380041004c0057006000690072007b0084008d009600a100ac00b500c000cb00d400df00e800f100fc0007011001190124012f01380141014a0153015c01650170017b0184018f019801a301ae01b701c001c901d201db01e401ed01f80103020e02170222022b021216505552434841534553494e565450434f53544558434852415441444a10434c53494e434f4d45434f4e534f4c49441053414c45534a4e4c1050555243484a4e4c10434153485245434a4e4c105041594d454e544a4e4c1053414c45534150504c50555243484150504c434f4d505256415410434f4d505243555354434f4d505256454e44434f4d505252455310434f4d505250524f4a5245434c4153534a4e4c1050485953494e564a4e4c10434f4d505242414e4b434f4d5052434845434b1046494e564f494443484b1052454d494e4445521041444a4144444355525210494e544552434f4d50554e415050454d504c554e41505053414c455310554e415050505552434810524556455253414c10454d504c4150504c105041594d545245434f4e10474c435552524556414c10415353454d424c591050524f4a474c4a4e4c50524f4a474c57495047454e2d444546455253414c2d44454645525055522d4445464552434f4e53554d504a4e4c10504f494e4f55544a4e4c10464c555348494e4710434150414349544a4e4c1050524f444f5244455250524f44554354494f4e10434f4d50524d41494e5410434f4d5052494e53105452414e5346455210524556414c4a4e4c10494e565441444a4d54494e56545243505410494e56545348505410494e56544f52444552434f4d5052494255444710574850485953494e565410574852434c53534a4e4c1057485055544157415957484d4f56454d454e5410434f4d505257485345";

    private static CompressedRecord ParseHex(string hex)
    {
        var page = new byte[PageHeader.PageLength];
        Convert.FromHexString(hex).CopyTo(page, 0);
        return CompressedRecord.Parse(page, 0);
    }

    private static string DecodeAscii(byte[]? bytes) =>
        bytes is null ? "<NULL>" : Encoding.ASCII.GetString(bytes).TrimEnd('\x10', '\0');

    [Fact]
    public void Parse_BaseRow_HasSevenSystemColumnsAndConsumesExactlyItsFreeDataBoundary()
    {
        var record = ParseHex(BaseRowHex);

        Assert.True(record.IsCdFormat);
        Assert.Equal(7, record.NumberOfColumns);
        Assert.True(record.HasLongDataRegion);
        // The captured hex already ends exactly at the page's own FreeData
        // boundary (176 absolute - 96 header = 80) -- if Length undershoots or
        // overshoots, a real page walk would either re-read part of this row
        // as if it were a second row, or skip real bytes.
        Assert.Equal(80, record.Length);
    }

    [Fact]
    public void Parse_BaseRow_SystemIdColumnIsA16ByteGuid()
    {
        var record = ParseHex(BaseRowHex);

        // Column 2 is LongData, 16 bytes -- the row's SystemId.
        Assert.Equal(ColumnIndicator.LongData, record.Indicators[2]);
        var systemId = record.GetColumnBytes(2);
        Assert.NotNull(systemId);
        Assert.Equal(16, systemId!.Length);
    }

    [Fact]
    public void Parse_BaseRow_CreatedAtAndModifiedAtAreEqualByteForByte()
    {
        // A freshly-seeded demo row was never updated after creation, so its
        // SystemCreatedAt and SystemModifiedAt columns (indices 3 and 5,
        // 7-byte datetime2 values) should be byte-identical -- this is an
        // independent semantic check that the short-data-region offsets are
        // right, not just that parsing didn't throw.
        var record = ParseHex(BaseRowHex);

        var createdAt = record.GetColumnBytes(3);
        var modifiedAt = record.GetColumnBytes(5);

        Assert.Equal(ColumnIndicator.SevenByte, record.Indicators[3]);
        Assert.Equal(ColumnIndicator.SevenByte, record.Indicators[5]);
        Assert.Equal(createdAt, modifiedAt);
    }

    [Fact]
    public void Parse_ExtRow_Has84ColumnsSpanningThreeShortDataClusters()
    {
        var record = ParseHex(ExtRowHex);

        // 84 columns needs ceil(84/30) = 3 short-data clusters. This is
        // exactly the shape OrcaMDF's own cluster-pointer sizing gets wrong
        // (see CompressedRecord.cs file header) -- decoding a column in the
        // 3rd cluster (index >= 60) without the fix throws
        // IndexOutOfRangeException instead of returning "REVALJNL".
        Assert.Equal(84, record.NumberOfColumns);
        Assert.Equal(867, record.Length);

        var thirdClusterColumn = record.GetColumnBytes(70); // "REVALJNL"
        Assert.NotNull(thirdClusterColumn);
    }

    [Theory]
    [InlineData(2, "SALES")]           // AL field 2 "Sales"
    [InlineData(3, "PURCHASES")]       // AL field 3 "Purchases"
    [InlineData(4, "INVTPCOST")]       // AL field 4 "Inventory Post Cost"
    [InlineData(5, "EXCHRATADJ")]      // AL field 5 "Exchange Rate Adjmt."
    [InlineData(10, "GENJNL")]         // AL field 10 "General Journal"
    [InlineData(61, "PRODORDER")]      // AL field 5502 "Production Order"
    [InlineData(82, "COMPRWHSE")]      // AL field 7307 "Compress Whse. Entries" (the LAST declared field --
                                       // physical columns run one further, to index 83 "SERVICE", which is
                                       // not any of the 80 currently-declared business fields: BC never
                                       // reclaims a dropped/obsoleted field's physical column)
    public void Parse_ExtRow_DecodesRecognisableSourceCodeValues(int columnIndex, string expected)
    {
        // Cross-checked against the table's AL schema pulled from the shipped
        // Base Application .app's SymbolReference.json (table id 242,
        // "Source Code Setup") -- these are real default demo values for the
        // CRONUS International Ltd. company, not just "bytes came out".
        var record = ParseHex(ExtRowHex);

        var bytes = record.GetColumnBytes(columnIndex);
        Assert.NotNull(bytes);
        Assert.Equal(expected, DecodeAscii(bytes!));
    }

    [Fact]
    public void Parse_ExtRow_UnsetBusinessFieldsDecodeAsEmptyNotNull()
    {
        // AL fields 6 "Post Recognition" and 7 "Post Value" (columns 6, 7)
        // are not configured on this demo company -- Code[10]'s AL default is
        // blank, which SQL stores as a real zero-length value, not NULL.
        var record = ParseHex(ExtRowHex);

        Assert.Equal(ColumnIndicator.ZeroByte, record.Indicators[6]);
        Assert.Equal(ColumnIndicator.ZeroByte, record.Indicators[7]);
        Assert.Equal(Array.Empty<byte>(), record.GetColumnBytes(6));
        Assert.Equal(Array.Empty<byte>(), record.GetColumnBytes(7));
    }

    [Fact]
    public void GetColumnBytes_ThrowsForDictionarySubstitutedColumns()
    {
        // No column in either captured row actually uses indicator 0xC
        // (DictionarySymbol) -- construct a minimal synthetic record that
        // does, to prove the "not implemented" path throws loudly instead of
        // returning wrong bytes silently.
        var page = new byte[PageHeader.PageLength];
        page[0] = 0x01;       // CD format, Primary, no long data
        page[1] = 0x01;       // numCols = 1
        // Indicator packing reads column 0 from the LOW nibble of the byte
        // right after the column count -- 0xC is DictionarySymbol.
        page[2] = 0x0C;

        var record = CompressedRecord.Parse(page, 0);

        Assert.Equal(ColumnIndicator.DictionarySymbol, record.Indicators[0]);
        Assert.Throws<NotSupportedException>(() => record.GetColumnBytes(0));
    }
}
