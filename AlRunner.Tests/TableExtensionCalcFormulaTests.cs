// TableExtensionCalcFormulaTests — issue #3121, differential 2.
//
// A FlowField a PRECOMPILED tableextension contributes carried NO CalcFormula through
// BcAppSymbolCache: TryParseTableExtensionSymbol dropped the property on purpose, under a
// comment claiming "Extension FlowFields with CalcFormulas don't exist in standard precompiled
// BC apps". Measured against Base Application 28.1 that is false — 36 of them do, including
// Customer 5912 "Outstanding Serv.Invoices(LCY)" and Stockkeeping Unit 99000777
// "Qty. on Prod. Order" — and every one of them reached NCLMetaField with
// NCLMetaCalculationFormula.EmptyFormula, so CalcFields refused it with BC's own
// "You must define a CalcFormula for the {0} FlowField in the {1} table".
//
// The BC-behaviour half of this (what those two fields actually calculate) is pinned upstream
// in the al-language corpus, where a real service tier adjudicates it. What is pinned HERE is
// the runner mechanism the corpus cannot see: the raw property text survives the symbol read,
// and it is carried as TEXT rather than as a parsed formula because that parse runs while
// RecordPatches may not be initialised — the SIGSEGV hazard that file's own header warns about.
//
// Same synthetic-.app strategy and the same serial collection as BcAppSymbolCacheTableExtTests;
// no Base Application closure is involved.

using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(CacheRootsSerialCollection.Name)]
public class TableExtensionCalcFormulaTests
{
    private static string WriteApp(string dir, string symbolReferenceJson)
    {
        var appPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".app");
        using var zip = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(zip, ZipArchiveMode.Create);
        var entry = za.CreateEntry("SymbolReference.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(symbolReferenceJson);
        return appPath;
    }

    private const string ExpectedFormula =
        "sum(\"Service Line\".\"Outstanding Amount (LCY)\" where(\"Document Type\" = const(Invoice), \"Bill-to Customer No.\" = field(\"No.\")))";

    /// <summary>
    /// The shape of Customer 5912, reduced: a FlowField WITH a CalcFormula, a FlowField
    /// without one, and an ordinary Normal column carrying neither. Only the first may produce
    /// a CalcFormulaTexts entry — a blanket "carry every property" would light up all three,
    /// which is what the two negative fields are here to exclude.
    /// </summary>
    private const string SymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "TableExtensions": [
            {
              "TargetObject": "#f3552374a1f24356848e196002525837#Customer",
              "Fields": [
                {
                  "TypeDefinition": { "Name": "Decimal" },
                  "Properties": [
                    { "Name": "FieldClass", "Value": "FlowField" },
                    { "Name": "CalcFormula", "Value": "sum(\"Service Line\".\"Outstanding Amount (LCY)\" where(\"Document Type\" = const(Invoice), \"Bill-to Customer No.\" = field(\"No.\")))" },
                    { "Name": "Editable", "Value": "0" }
                  ],
                  "Id": 5912,
                  "Name": "Outstanding Serv.Invoices(LCY)"
                },
                {
                  "TypeDefinition": { "Name": "Decimal" },
                  "Properties": [
                    { "Name": "FieldClass", "Value": "FlowField" }
                  ],
                  "Id": 5913,
                  "Name": "Formula-less FlowField"
                },
                {
                  "TypeDefinition": { "Name": "Code[10]" },
                  "Properties": [
                    { "Name": "Caption", "Value": "Plain" }
                  ],
                  "Id": 5914,
                  "Name": "Plain Column"
                }
              ],
              "Id": 5900,
              "Name": "ServiceCustomerExt"
            }
          ]
        }
        """;

    [Fact]
    public void GetTableExtensions_FlowFieldWithCalcFormula_CarriesTheRawPropertyText()
    {
        var dir = TestScratch.Dir("al-runner-tableext-calcformula");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);

            var ext = Assert.Single(BcAppSymbolCache.GetTableExtensions(appPath));

            Assert.Equal("Customer", ext.TargetTableName);
            Assert.NotNull(ext.CalcFormulaTexts);

            // The exact text, not merely "something non-empty": the consumer hands it to
            // RecordPatches.TryParseCalcFormula as `CalcFormula = <text>;`, so a truncated or
            // re-quoted value parses to a DIFFERENT formula rather than failing loudly.
            Assert.Equal(ExpectedFormula, ext.CalcFormulaTexts![5912]);

            var flowField = ext.Fields.Single(f => f.FieldId == 5912);
            Assert.True(flowField.IsFlowField);
            // Deliberately still null here: parsing at symbol-read time would call into
            // RecordPatches while AddBcAppPath is registering the .app. The merge step parses it.
            Assert.Null(flowField.CalcFormula);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void GetTableExtensions_FieldsWithoutACalcFormula_GetNoEntry()
    {
        var dir = TestScratch.Dir("al-runner-tableext-calcformula-neg");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);

            var ext = Assert.Single(BcAppSymbolCache.GetTableExtensions(appPath));

            Assert.NotNull(ext.CalcFormulaTexts);
            Assert.Single(ext.CalcFormulaTexts!);
            Assert.False(ext.CalcFormulaTexts!.ContainsKey(5913));  // FlowField, no formula
            Assert.False(ext.CalcFormulaTexts!.ContainsKey(5914));  // Normal column

            // The formula-less FlowField still arrives AS a FlowField, so CalcFields still
            // refuses it with BC's "must define a CalcFormula" — the #3079 behaviour this fix
            // must not weaken.
            Assert.True(ext.Fields.Single(f => f.FieldId == 5913).IsFlowField);
            Assert.False(ext.Fields.Single(f => f.FieldId == 5914).IsFlowField);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void GetTableExtensions_NoFlowFieldCarriesAFormula_LeavesTheMapNull()
    {
        var dir = TestScratch.Dir("al-runner-tableext-calcformula-none");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, """
                {
                  "RuntimeVersion": "15.1",
                  "TableExtensions": [
                    {
                      "TargetObject": "Source Code Setup",
                      "Fields": [
                        {
                          "TypeDefinition": { "Name": "Code[10]" },
                          "Properties": [ { "Name": "Caption", "Value": "Sales" } ],
                          "Id": 2,
                          "Name": "Sales"
                        }
                      ],
                      "Id": 243,
                      "Name": "SourceCodeSetupExt"
                    }
                  ]
                }
                """);

            var ext = Assert.Single(BcAppSymbolCache.GetTableExtensions(appPath));

            // Null rather than an empty dictionary: the merge step short-circuits on it and
            // returns the original field list untouched, so an extension with no FlowField
            // formula allocates nothing.
            Assert.Null(ext.CalcFormulaTexts);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
