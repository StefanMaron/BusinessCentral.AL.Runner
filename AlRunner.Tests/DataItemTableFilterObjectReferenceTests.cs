// DataItemTableFilterObjectReferenceTests — issue #5418.
//
// A query shipped PRECOMPILED in a dependency states its DataItemTableFilter as AL text in
// SymbolReference.json, and the runner builds the query's MetaQuery design from that text
// (RecordPatches.NclMetaQueryBuilder.BuildMetaQueryDesign). Base Application's query 522
// "Qty. Reserved From Item Ledger" states
//
//   Positive = const(true),
//   "Source Type" = const(Database::"Item Ledger Entry"),
//   "Reservation Status" = const(Reservation)
//
// and BC's own evaluator raised "The value "Database::"Item Ledger Entry"" can't be evaluated
// into type Integer" on the first filter call, because the object reference reached it as text.
//
// The BC-behaviour half (such a filter selects the rows its table id names) is asked upstream:
// corpus codeunit 69980, query/TestQueryDataItemTableFilterDatabaseConst.al. What this pins is the
// runner's own seam, RecordPatches.DataItemTableFilterValue — the text each parsed condition
// hands to BC — on the precompiled-dependency route no source-compiled corpus fixture reaches,
// over a fixture symbol app so no Base Application is loaded (no-base-app-in-csharp-tests.md).
using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// MUST be serial: registering a symbol .app mutates RecordPatches' process-global _bcAppPaths and
// the table index derived from it, and Dispose calls ResetForReload().
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class DataItemTableFilterObjectReferenceTests : IDisposable
{
    private readonly string _root;

    public DataItemTableFilterObjectReferenceTests()
    {
        _root = TestScratch.Dir("al-runner-dataitem-filter-objref");
        Directory.CreateDirectory(_root);
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(WriteSymbolApp());
    }

    public void Dispose()
    {
        try { RecordPatches.ResetForReload(); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteSymbolApp()
    {
        const string SymbolReference = """
        {
          "AppId": "6a4f2f1e-2c53-4b0e-9f1a-0b7d1f9a3c52",
          "Name": "DTF ObjRef Fixture",
          "Publisher": "AL Runner",
          "Version": "1.0.0.0",
          "Tables": [
            { "Id": 70961, "Name": "DTF Ledger Entry",
              "Fields": [ { "Id": 1, "Name": "Entry No.", "TypeDefinition": { "Name": "Integer" } } ] }
          ],
          "Reports": [], "Codeunits": [], "Pages": [], "Queries": [], "XmlPorts": [], "EnumTypes": []
        }
        """;

        var appPath = Path.Combine(_root, "dtf-objref-fixture.app");
        using var fs = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(fs, ZipArchiveMode.Create);
        using var w = new StreamWriter(za.CreateEntry("SymbolReference.json").Open(), Encoding.UTF8);
        w.Write(SymbolReference);
        return appPath;
    }

    // Query 522's property as the symbol file states it: a CRLF and a run of spaces between the
    // conditions, copied from Base Application 28.5.54151.55132.
    private const string Query522Style =
        "Positive = const(true),\r\n                                      "
        + "\"Source Type\" = const(Database::\"DTF Ledger Entry\"),\r\n                                      "
        + "\"Reservation Status\" = const(Reservation)";

    [Fact]
    public void DataItemTableFilterValue_ResolvesTheDatabaseConst_AndLeavesTheOtherConditionsAlone()
    {
        var parsed = RecordPatches.TryParseColumnFilterText(Query522Style);
        Assert.NotNull(parsed);

        var values = parsed!.ToDictionary(c => c.FieldName, RecordPatches.DataItemTableFilterValue);

        // The object reference becomes the table's id; the neighbours — a boolean and an enum
        // member, evaluated by BC against the field's own type — come back byte-identical.
        Assert.Equal("70961", values["Source Type"]);
        Assert.Equal("true", values["Positive"]);
        Assert.Equal("Reservation", values["Reservation Status"]);
    }

    [Fact]
    public void DataItemTableFilterValue_UnknownTable_KeepsTheTextSoBcRefusesItByName()
    {
        var parsed = RecordPatches.TryParseColumnFilterText("\"Source Type\" = const(Database::\"DTF No Such Table\")");
        Assert.NotNull(parsed);

        // loud-failures.md: neither 0 nor a dropped condition; BC's evaluator then names the text.
        Assert.Equal("Database::\"DTF No Such Table\"",
            RecordPatches.DataItemTableFilterValue(Assert.Single(parsed!)));
    }
}
