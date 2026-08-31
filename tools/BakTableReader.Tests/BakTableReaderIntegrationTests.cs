// Opt-in end-to-end proof for AL Runner issue #2241: reads the REAL demo .bak
// from a sandbox artifact and decodes "Source Code Setup" for the CRONUS
// International Ltd. company, using nothing but this project.
//
// Gated on AL_RUNNER_SPIKE_BAK_PATH because the .bak (~900MB) does not exist
// in CI -- every fact here calls Skip.If first and is a real Skipped result,
// not a silently-passing early return, when the file is absent.
using System.Linq;
using System.Text;
using BakTableReader.RecordDecoding;
using Xunit;

namespace BakTableReader.Tests;

public class BakTableReaderIntegrationTests
{
    private static string? BakPath => Environment.GetEnvironmentVariable("AL_RUNNER_SPIKE_BAK_PATH");

    [SkippableFact]
    public void ReadsSourceCodeSetupForCronusFromTheRealDemoBak()
    {
        Skip.If(string.IsNullOrEmpty(BakPath) || !File.Exists(BakPath),
            "set AL_RUNNER_SPIKE_BAK_PATH to a BC sandbox artifact's " +
            "w1/BusinessCentral-W1.bak to run this against real data (see issue #2241)");

        using var file = BakTableReader.BakFile.Open(BakPath!);
        var catalog = new SqlCatalog(file);

        var baseObject = catalog.SysSchObjs.Single(o =>
            o.Type == "U " && o.Name.StartsWith("CRONUS International Ltd_$Source Code Setup$") &&
            !o.Name.EndsWith("$ext"));
        var extObject = catalog.SysSchObjs.Single(o =>
            o.Type == "U " && o.Name.StartsWith("CRONUS International Ltd_$Source Code Setup$") &&
            o.Name.EndsWith("$ext"));

        var (baseRowset, baseAu) = catalog.GetTableStorage(baseObject.Id);
        Assert.Equal(1, baseRowset.RowCount); // Source Code Setup is a singleton
        Assert.True(baseRowset.CompressionLevel >= 1, "expected the real demo data to be compressed");

        var baseRow = catalog.WalkCompressedRows(baseAu.PgFirst).Single();
        Assert.Equal(7, baseRow.NumberOfColumns); // timestamp + PK + 5 system columns

        var (extRowset, extAu) = catalog.GetTableStorage(extObject.Id);
        var extRow = catalog.WalkCompressedRows(extAu.PgFirst).Single();
        Assert.Equal(84, extRow.NumberOfColumns);

        // Ground truth is the table's AL schema (field numbers/names), not
        // eyeballed strings -- see the PR description for how it was pulled
        // from the shipped Base Application .app's SymbolReference.json
        // (table id 242, field 2 "Sales" is business-field position 0, and
        // the ext table's physical column order matches AL field declaration
        // order starting at physical column 2).
        Assert.Equal("SALES", Ascii(extRow, 2));
        Assert.Equal("PURCHASES", Ascii(extRow, 3));
        Assert.Equal("INVTPCOST", Ascii(extRow, 4));
    }

    private static string Ascii(CompressedRecord record, int columnIndex)
    {
        var bytes = record.GetColumnBytes(columnIndex);
        Assert.NotNull(bytes);
        return Encoding.ASCII.GetString(bytes!).TrimEnd('\x10', '\0');
    }
}
