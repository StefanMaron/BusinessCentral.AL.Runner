// DependencyReportAutoCalcFieldTests — #4648. A precompiled report's FlowField column must
// carry AutoCalcField into the runtime metadata document, or BC's DataItem.FindCalcFields
// skips it and the dataset reads 0.
//
// A RUNNER-MECHANISM test. The BC-behaviour claim (a FlowField column is calculated with no
// CalcFields anywhere) is adjudicated upstream by corpus codeunit 60935 over Base Application
// report 5620. What is pinned here is the runner's own pipeline: the symbol reader carries the
// property, the dependency document states it, and BC's OWN reader
// (Types.Metadata.MetaDataItemColumn) reads the stated value back. The second class pins the
// premise the fix rests on: BC's emitter states AutoCalcField on every column, default
// included.

using System.IO.Compression;
using System.Text;
using System.Xml;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Types.Metadata;
using Xunit;

namespace AlRunner.Tests;

// BcAppSymbolCache.Get() resolves its on-disk path through the process-global CacheRoots
// override (#1821), like the other DependencyReport*Tests.
[Collection(CacheRootsSerialCollection.Name)]
public sealed class DependencyReportAutoCalcFieldTests
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

    // Report 35's "Purch. Inv. Header" column shape as Base Application 28.1.49838.53910's symbol
    // file states it: Amt_PurchInvHeader carries IncludeCaption and NO AutoCalcField. The other
    // two columns state the property both ways, as 134 columns in that file do (133 "0", one "1").
    private const string SymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Namespaces": [
            {
              "Name": "Foundation",
              "Reports": [
                {
                  "Id": 35,
                  "Name": "Document Entries",
                  "DataItems": [
                    {
                      "Id": 1,
                      "Name": "Purch. Inv. Header",
                      "RelatedTable": "Purch. Inv. Header",
                      "Columns": [
                        { "OwningDataItemName": "Purch. Inv. Header", "TypeDefinition": { "Name": "Decimal" },
                          "Properties": [ { "Name": "IncludeCaption", "Value": "1" } ],
                          "Id": 964472448, "Name": "Amt_PurchInvHeader" },
                        { "OwningDataItemName": "Purch. Inv. Header", "TypeDefinition": { "Name": "Decimal" },
                          "Properties": [ { "Name": "AutoCalcField", "Value": "0" } ],
                          "Id": 11, "Name": "NotCalculated" },
                        { "OwningDataItemName": "Purch. Inv. Header", "TypeDefinition": { "Name": "Decimal" },
                          "Properties": [ { "Name": "AutoCalcField", "Value": "1" } ],
                          "Id": 12, "Name": "StatedCalculated" }
                      ]
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private static BcAppSymbolCache.ReportSymbol LoadReport(string dir)
        => Assert.Single(BcAppSymbolCache.Get(WriteApp(dir, SymbolReference)).Reports, r => r.Id == 35);

    [Fact]
    public void ReportColumn_CarriesAutoCalcField_DefaultTrue_StatedFalseKept()
    {
        var dir = TestScratch.Dir("al-runner-dep-report-autocalc-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var columns = Assert.Single(LoadReport(dir).DataItems).Columns;

            // Unstated is AL's default, true — the case of every Base Application FlowField
            // column behind #4648.
            Assert.True(columns.Single(c => c.Name == "Amt_PurchInvHeader").AutoCalcField);
            // A stated false must survive; defaulting it would calculate a column the AL
            // author switched off.
            Assert.False(columns.Single(c => c.Name == "NotCalculated").AutoCalcField);
            Assert.True(columns.Single(c => c.Name == "StatedCalculated").AutoCalcField);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReportMetadataXml_BcsOwnColumnReader_SeesTheAutoCalcField()
    {
        var dir = TestScratch.Dir("al-runner-dep-report-autocalc-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var xml = RecordPatches.EmitReportXml(LoadReport(dir), sourceExprByColumn: null);
            var doc = new XmlDocument();
            doc.LoadXml(xml);

            var read = new Dictionary<string, bool>();
            foreach (XmlNode field in doc.SelectNodes("//DataItemField")!)
            {
                // The reader DataItem.FindCalcFields consults. It leaves AutoCalcField false
                // when the element is missing, which is the whole defect.
                var column = new MetaDataItemColumn(field, 0);
                read[column.Name] = column.AutoCalcField;
            }

            Assert.Equal(3, read.Count);
            Assert.True(read["Amt_PurchInvHeader"], "an unstated AutoCalcField must reach BC as true");
            Assert.False(read["NotCalculated"], "a stated AutoCalcField = false must reach BC as false");
            Assert.True(read["StatedCalculated"], "a stated AutoCalcField = true must reach BC as true");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SymbolCacheKey_CarriesTheNewMember_SoAnOldCacheEntryRekeys()
    {
        // A cache payload written before #4648 has no AutoCalcField, and would deserialize to
        // false for every column. The key carries the payload's shape, so it must name the
        // member: then a pre-#4648 entry is a miss rather than a replay.
        var payload = typeof(BcAppSymbolCache).GetNestedType("CachePayload",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            ?? throw new InvalidOperationException("BcAppSymbolCache.CachePayload not found");
        var shape = AlRunner.Infrastructure.RecordShapeFingerprint.Describe(payload);
        Assert.Contains("ReportColumnSymbol", shape);
        Assert.Contains("AutoCalcField", shape);
    }
}

// The premise: BC's own emitter states AutoCalcField on every report column, "1" when the AL
// says nothing and "0" when it says false. EmitReportXml writes the same pair.
[Collection(BcEngineCollection.Name)]
public sealed class ReportColumnAutoCalcFieldEmitTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public ReportColumnAutoCalcFieldEmitTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-report-autocalc-emit-tests");
        Directory.CreateDirectory(_root);
        AlReportMetadataRegistry.Clear();
    }

    public void Dispose()
    {
        AlReportMetadataRegistry.Clear();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private const string FixtureAl = """
        table 90320 "AcfEmit Entry"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "Entry No."; Integer) { DataClassification = CustomerContent; }
                field(2; "Head No."; Integer) { DataClassification = CustomerContent; }
                field(3; Amount; Decimal) { DataClassification = CustomerContent; }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }

        table 90321 "AcfEmit Head"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "No."; Integer) { DataClassification = CustomerContent; }
                field(2; Total; Decimal)
                {
                    FieldClass = FlowField;
                    CalcFormula = sum("AcfEmit Entry".Amount where("Head No." = field("No.")));
                }
            }
            keys { key(PK; "No.") { Clustered = true; } }
        }

        report 90322 "AcfEmit Report"
        {
            UsageCategory = None;
            dataset
            {
                dataitem(Head; "AcfEmit Head")
                {
                    column(DefaultTotal; Total) { }
                    column(OffTotal; Total) { AutoCalcField = false; }
                }
            }
        }
        """;

    [SkippableFact]
    public void BcEmitter_StatesAutoCalcField_OnEveryColumn()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        File.WriteAllText(Path.Combine(_root, "AcfEmit.al"), FixtureAl);
        var output = new BcCompiler().Emit(new[] { _root }, "AcfEmitModule");
        Assert.True(output.Sources.Count > 0,
            $"Expected the report to emit; diagnostics: {string.Join(" | ", output.Diagnostics.Take(10))}");
        Assert.True(AlReportMetadataRegistry.TryGet(90322, out var xml), "report 90322's document was not captured");

        var doc = new XmlDocument();
        doc.LoadXml(xml);
        var stated = new Dictionary<string, string>();
        foreach (XmlNode field in doc.SelectNodes("//DataItemField")!)
        {
            var name = field.SelectSingleNode("FriendlyFieldName")?.InnerText;
            var acf = field.SelectSingleNode("AutoCalcField")?.InnerText;
            if (name is "DefaultTotal" or "OffTotal") stated[name] = acf ?? "<absent>";
        }

        Assert.Equal("1", stated["DefaultTotal"]);
        Assert.Equal("0", stated["OffTotal"]);
    }
}
