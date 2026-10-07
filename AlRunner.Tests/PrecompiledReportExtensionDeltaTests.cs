// Issue #4837: the data items a PRECOMPILED reportextension adds are merged into its report's
// MetaReport, through BC's own delta applicator, from a document the runner derives out of the
// extension's SymbolReference.json entry and its AL source (RecordPatches.
// TryBuildPrecompiledReportExtensionDelta). These pin the derivation and what is refused.
//
// The end-to-end claim (the added data item iterates, its table view and triggers apply) is the
// corpus's: codeunit 68760, Base Application report 302 with reportextension 929. This file pins
// what a corpus test cannot see: the document's shape against what BC's emitter writes for the
// same extension, the operation each add keyword maps to, and every case that must stay unmerged
// rather than be guessed.
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(CacheRootsSerialCollection.Name)]
public class PrecompiledReportExtensionDeltaTests
{
    // One id range per test: the registered .app list is process-wide, so a second test reusing an
    // id would see the first one's report and extension too.
    private static int _next = 88493000;
    private static int NextRange() => Interlocked.Add(ref _next, 10);

    private sealed record Fixture(int ReportId, int ExtensionId, string ReportName, string ExtensionName, string TableName);

    private static Fixture NewFixture(int? range = null)
    {
        var n = range ?? NextRange();
        return new Fixture(n + 1, n + 2, $"PRX Report {n}", $"PRX Ext {n}", $"PRX Sample {n}");
    }

    private static string DataItemJson(string name, string owner, int indentation, int id, string table, string properties = "[]", string columns = "[]", string children = "[]")
        => $$"""
            { "OwningDataItemName": "{{owner}}", "RelatedTable": "{{table}}", "Indentation": {{indentation}}, "FilterControlId": 1,
              "Columns": {{columns}}, "DataItems": {{children}}, "Properties": {{properties}}, "Id": {{id}}, "Name": "{{name}}" }
            """;

    private static string SymbolReference(Fixture f, int tableId, string extensionDataItems, string extensionColumns = "[]", string sourceFile = "src/Ext.ReportExt.al")
        => $$"""
            {
              "RuntimeVersion": "17.0",
              "Tables": [
                { "Id": {{tableId}}, "Name": "{{f.TableName}}", "Properties": [],
                  "Fields": [
                    { "TypeDefinition": { "Name": "Integer" }, "Properties": [], "Id": 1, "Name": "Entry No." },
                    { "TypeDefinition": { "Name": "Text[50]" }, "Properties": [], "Id": 2, "Name": "Description" },
                    { "TypeDefinition": { "Name": "Code[20]" }, "Properties": [], "Id": 5, "Name": "Alt Code" },
                    { "TypeDefinition": { "Name": "Decimal" }, "Properties": [], "Id": 6, "Name": "Amount" }
                  ] }
              ],
              "Reports": [
                { "Id": {{f.ReportId}}, "Name": "{{f.ReportName}}", "RequestPage": { "Id": 0, "Name": "RequestOptionsPage" },
                  "DataItems": [
                    { "RelatedTable": "{{f.TableName}}", "Indentation": 0, "Columns": [], "Properties": [], "Id": 1369927887, "Name": "Src",
                      "DataItems": [
                        {{DataItemJson("Child1", "Src", 1, 343490586, f.TableName)}},
                        {{DataItemJson("Child2", "Src", 1, 981187203, f.TableName)}}
                      ] }
                  ] }
              ],
              "ReportExtensions": [
                { "Id": {{f.ExtensionId}}, "Name": "{{f.ExtensionName}}", "Target": "{{f.ReportName}}",
                  "RequestPage": { "ControlChanges": [] },
                  "DataItems": {{extensionDataItems}}, "Columns": {{extensionColumns}},
                  "ReferenceSourceFileName": "{{sourceFile}}" }
              ]
            }
            """;

    private static string ExtensionSource(Fixture f, string dataset)
        => $$"""
            reportextension {{f.ExtensionId}} "{{f.ExtensionName}}" extends "{{f.ReportName}}"
            {
                dataset
                {
            {{dataset}}
                }
            }
            """;

    private static void WithApp(Fixture f, string symbolReference, string? source, Action<string> body)
    {
        var dir = TestScratch.Dir("al-runner-precompiled-report-extension-delta-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".app");
            using (var zip = new FileStream(appPath, FileMode.Create))
            using (var za = new ZipArchive(zip, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(za.CreateEntry("SymbolReference.json").Open(), Encoding.UTF8))
                    w.Write(symbolReference);
                if (source != null)
                    using (var w = new StreamWriter(za.CreateEntry("src/Ext.ReportExt.al").Open(), Encoding.UTF8))
                        w.Write(source);
            }
            RecordPatches.AddBcAppPath(appPath);
            body(appPath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string? Delta(Fixture f, out string? reason)
    {
        var (report, extension, source) = RecordPatches.PrecompiledReportExtensionsOf(f.ReportId).Single();
        return RecordPatches.TryBuildPrecompiledReportExtensionDelta(report, extension, source, out reason);
    }

    // ── the shape BC's emitter writes ─────────────────────────────────────────────────────────

    // BC 28.5.54151.55132's emitter, over this reportextension (report 90411 "ZZ Base" with Src and
    // its two children Child1 and Child2), captured with the request page's ControlAdd/Expression
    // blocks and the tooltip translation keys cut: the shape a source-compiled extension's
    // document has, which NavReportSync.ApplyReportExtensionDeltas applies unchanged.
    //     addlast(Src)     { ExtHdr (view, link, three columns) { ExtLine (link, a column) } }
    //     addfirst(Src)    { ExtFirst }
    //     addafter(Child1) { ExtAfter }
    //     addbefore(Child2){ ExtBefore }
    private const string BcEmittedDocument = """
        <ReportExtension xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema" ALNamespace="" xmlns="urn:schemas-microsoft-com:dynamics:NAV:MetaObjects">
          <MetadataVersion>130000</MetadataVersion>
          <ID>90412</ID>
          <Name>ZZ Ext</Name>
          <DataItemAdd>
            <AnchorName>Src</AnchorName>
            <AnchorId>1369927887</AnchorId>
            <Operation>AddLast</Operation>
            <DataItem>
              <MaxIteration>0</MaxIteration>
              <PrintOnlyIfDetail>0</PrintOnlyIfDetail>
              <DataItemLink>Field1=FIELD(Field1)</DataItemLink>
              <DataItemTableView>SORTING(Field1) WHERE(Field1=1(&lt;&gt;0))</DataItemTableView>
              <DataItemTable>90410</DataItemTable>
              <DataItemIndent>1</DataItemIndent>
              <ID>378700002</ID>
              <DataItemVarName>ExtHdr</DataItemVarName>
              <DataItemViewName>ReportExtension90412DataItem0TableView</DataItemViewName>
              <DataItemLinkReference>Src</DataItemLinkReference>
              <FieldReferences>1,2,6</FieldReferences>
              <DataItemField>
                <AutoCalcField>1</AutoCalcField>
                <ID>1439463707</ID>
                <FriendlyFieldName>ExtHdrNo</FriendlyFieldName>
                <FieldType>Integer</FieldType>
                <FieldNo>1</FieldNo>
                <SourceExpr>"Entry No."</SourceExpr>
              </DataItemField>
              <DataItemField>
                <AutoCalcField>1</AutoCalcField>
                <ID>914509453</ID>
                <FriendlyFieldName>ExtHdrDesc</FriendlyFieldName>
                <FieldType>Text</FieldType>
                <FieldNo>2</FieldNo>
                <SourceExpr>Description</SourceExpr>
              </DataItemField>
              <DataItemField>
                <AutoCalcField>1</AutoCalcField>
                <ID>1394413973</ID>
                <FriendlyFieldName>ExtHdrComputed</FriendlyFieldName>
                <FieldType>String</FieldType>
                <FieldNo>-1</FieldNo>
                <SourceExpr>Format(Amount)</SourceExpr>
              </DataItemField>
            </DataItem>
          </DataItemAdd>
          <DataItemAdd>
            <AnchorName>ExtHdr</AnchorName>
            <AnchorId>378700002</AnchorId>
            <Operation>AddLast</Operation>
            <DataItem>
              <MaxIteration>0</MaxIteration>
              <PrintOnlyIfDetail>0</PrintOnlyIfDetail>
              <DataItemLink>Field5=FIELD(Field5)</DataItemLink>
              <DataItemTable>90410</DataItemTable>
              <DataItemIndent>2</DataItemIndent>
              <ID>1843858974</ID>
              <DataItemVarName>ExtLine</DataItemVarName>
              <DataItemViewName>ReportExtension90412DataItem1TableView</DataItemViewName>
              <DataItemLinkReference>ExtHdr</DataItemLinkReference>
              <FieldReferences>5</FieldReferences>
              <DataItemField>
                <AutoCalcField>1</AutoCalcField>
                <ID>450473611</ID>
                <FriendlyFieldName>ExtLineKey</FriendlyFieldName>
                <FieldType>Code</FieldType>
                <FieldNo>5</FieldNo>
                <SourceExpr>"Alt Code"</SourceExpr>
              </DataItemField>
            </DataItem>
          </DataItemAdd>
          <DataItemAdd>
            <AnchorName>Src</AnchorName>
            <AnchorId>1369927887</AnchorId>
            <Operation>AddFirst</Operation>
            <DataItem>
              <MaxIteration>0</MaxIteration>
              <PrintOnlyIfDetail>0</PrintOnlyIfDetail>
              <DataItemTable>90410</DataItemTable>
              <DataItemIndent>1</DataItemIndent>
              <ID>1678737158</ID>
              <DataItemVarName>ExtFirst</DataItemVarName>
              <DataItemViewName>ReportExtension90412DataItem2TableView</DataItemViewName>
              <DataItemLinkReference>Src</DataItemLinkReference>
              <FieldReferences>1</FieldReferences>
              <DataItemField>
                <AutoCalcField>1</AutoCalcField>
                <ID>257582913</ID>
                <FriendlyFieldName>ExtFirstNo</FriendlyFieldName>
                <FieldType>Integer</FieldType>
                <FieldNo>1</FieldNo>
                <SourceExpr>"Entry No."</SourceExpr>
              </DataItemField>
            </DataItem>
          </DataItemAdd>
          <DataItemAdd>
            <AnchorName>Child1</AnchorName>
            <AnchorId>343490586</AnchorId>
            <Operation>AddAfter</Operation>
            <DataItem>
              <MaxIteration>0</MaxIteration>
              <PrintOnlyIfDetail>0</PrintOnlyIfDetail>
              <DataItemTable>90410</DataItemTable>
              <DataItemIndent>1</DataItemIndent>
              <ID>533819344</ID>
              <DataItemVarName>ExtAfter</DataItemVarName>
              <DataItemViewName>ReportExtension90412DataItem3TableView</DataItemViewName>
              <DataItemLinkReference>Src</DataItemLinkReference>
              <FieldReferences>1</FieldReferences>
              <DataItemField>
                <AutoCalcField>1</AutoCalcField>
                <ID>1618849833</ID>
                <FriendlyFieldName>ExtAfterNo</FriendlyFieldName>
                <FieldType>Integer</FieldType>
                <FieldNo>1</FieldNo>
                <SourceExpr>"Entry No."</SourceExpr>
              </DataItemField>
            </DataItem>
          </DataItemAdd>
          <DataItemAdd>
            <AnchorName>Child2</AnchorName>
            <AnchorId>981187203</AnchorId>
            <Operation>AddBefore</Operation>
            <DataItem>
              <MaxIteration>0</MaxIteration>
              <PrintOnlyIfDetail>0</PrintOnlyIfDetail>
              <DataItemTable>90410</DataItemTable>
              <DataItemIndent>1</DataItemIndent>
              <ID>73650211</ID>
              <DataItemVarName>ExtBefore</DataItemVarName>
              <DataItemViewName>ReportExtension90412DataItem4TableView</DataItemViewName>
              <DataItemLinkReference>Src</DataItemLinkReference>
              <FieldReferences>1</FieldReferences>
              <DataItemField>
                <AutoCalcField>1</AutoCalcField>
                <ID>2012721092</ID>
                <FriendlyFieldName>ExtBeforeNo</FriendlyFieldName>
                <FieldType>Integer</FieldType>
                <FieldNo>1</FieldNo>
                <SourceExpr>"Entry No."</SourceExpr>
              </DataItemField>
            </DataItem>
          </DataItemAdd>
        </ReportExtension>
        """;

    // The same capture for a block that declares several data items:
    //     addbefore(Child2) { XA; XB; XC }     addafter(Child1) { YA; YB }
    // The first of a block takes the block's operation and anchor; each further one is AddAfter the
    // one before it. (Probe table 90410 with one field; Src, Child1, Child2 as above.)
    private const string BcEmittedMultiRootDocument = """
        <ReportExtension xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema" ALNamespace="" xmlns="urn:schemas-microsoft-com:dynamics:NAV:MetaObjects">
          <MetadataVersion>130000</MetadataVersion>
          <ID>90412</ID>
          <Name>ZZ Ext</Name>
          <DataItemAdd>
            <AnchorName>Child2</AnchorName>
            <AnchorId>981187203</AnchorId>
            <Operation>AddBefore</Operation>
            <DataItem>
              <MaxIteration>0</MaxIteration>
              <PrintOnlyIfDetail>0</PrintOnlyIfDetail>
              <DataItemTable>90410</DataItemTable>
              <DataItemIndent>1</DataItemIndent>
              <ID>504038852</ID>
              <DataItemVarName>XA</DataItemVarName>
              <DataItemViewName>ReportExtension90412DataItem0TableView</DataItemViewName>
              <DataItemLinkReference>Src</DataItemLinkReference>
            </DataItem>
          </DataItemAdd>
          <DataItemAdd>
            <AnchorName>XA</AnchorName>
            <AnchorId>504038852</AnchorId>
            <Operation>AddAfter</Operation>
            <DataItem>
              <MaxIteration>0</MaxIteration>
              <PrintOnlyIfDetail>0</PrintOnlyIfDetail>
              <DataItemTable>90410</DataItemTable>
              <DataItemIndent>1</DataItemIndent>
              <ID>1877838593</ID>
              <DataItemVarName>XB</DataItemVarName>
              <DataItemViewName>ReportExtension90412DataItem1TableView</DataItemViewName>
              <DataItemLinkReference>Src</DataItemLinkReference>
            </DataItem>
          </DataItemAdd>
          <DataItemAdd>
            <AnchorName>XB</AnchorName>
            <AnchorId>1877838593</AnchorId>
            <Operation>AddAfter</Operation>
            <DataItem>
              <MaxIteration>0</MaxIteration>
              <PrintOnlyIfDetail>0</PrintOnlyIfDetail>
              <DataItemTable>90410</DataItemTable>
              <DataItemIndent>1</DataItemIndent>
              <ID>1779432086</ID>
              <DataItemVarName>XC</DataItemVarName>
              <DataItemViewName>ReportExtension90412DataItem2TableView</DataItemViewName>
              <DataItemLinkReference>Src</DataItemLinkReference>
            </DataItem>
          </DataItemAdd>
          <DataItemAdd>
            <AnchorName>Child1</AnchorName>
            <AnchorId>343490586</AnchorId>
            <Operation>AddAfter</Operation>
            <DataItem>
              <MaxIteration>0</MaxIteration>
              <PrintOnlyIfDetail>0</PrintOnlyIfDetail>
              <DataItemTable>90410</DataItemTable>
              <DataItemIndent>1</DataItemIndent>
              <ID>434355077</ID>
              <DataItemVarName>YA</DataItemVarName>
              <DataItemViewName>ReportExtension90412DataItem3TableView</DataItemViewName>
              <DataItemLinkReference>Src</DataItemLinkReference>
            </DataItem>
          </DataItemAdd>
          <DataItemAdd>
            <AnchorName>YA</AnchorName>
            <AnchorId>434355077</AnchorId>
            <Operation>AddAfter</Operation>
            <DataItem>
              <MaxIteration>0</MaxIteration>
              <PrintOnlyIfDetail>0</PrintOnlyIfDetail>
              <DataItemTable>90410</DataItemTable>
              <DataItemIndent>1</DataItemIndent>
              <ID>1072051694</ID>
              <DataItemVarName>YB</DataItemVarName>
              <DataItemViewName>ReportExtension90412DataItem4TableView</DataItemViewName>
              <DataItemLinkReference>Src</DataItemLinkReference>
            </DataItem>
          </DataItemAdd>
          <Labels />
          <Layouts />
        </ReportExtension>
        """;

    private const string ZzDataset = """
                addlast(Src)
                {
                    dataitem(ExtHdr; "PRX Sample")
                    {
                        DataItemTableView = sorting("Entry No.") where("Entry No." = filter(<> 0));
                        DataItemLink = "Entry No." = field("Entry No.");
                        column(ExtHdrNo; "Entry No.") { IncludeCaption = true; }
                        column(ExtHdrDesc; Description) { }
                        column(ExtHdrComputed; Format(Amount)) { }
                        dataitem(ExtLine; "PRX Sample")
                        {
                            DataItemLink = "Alt Code" = field("Alt Code");
                            column(ExtLineKey; "Alt Code") { }
                        }
                    }
                }
                addfirst(Src)
                {
                    dataitem(ExtFirst; "PRX Sample") { column(ExtFirstNo; "Entry No.") { } }
                }
                addafter(Child1)
                {
                    dataitem(ExtAfter; "PRX Sample") { column(ExtAfterNo; "Entry No.") { } }
                }
                addbefore(Child2)
                {
                    dataitem(ExtBefore; "PRX Sample") { column(ExtBeforeNo; "Entry No.") { } }
                }
        """;

    private static string ColumnJson(string name, int id, string type)
        => $$"""{ "OwningDataItemName": "x", "TypeDefinition": { "Name": "{{type}}" }, "Id": {{id}}, "Name": "{{name}}" }""";

    private static string ZzExtensionDataItems(string table)
    {
        var line = DataItemJson("ExtLine", "ExtHdr", 2, 1843858974, table,
            properties: """[ { "Name": "DataItemLink", "Value": "\"Alt Code\" = field(\"Alt Code\")" } ]""",
            columns: $"[ {ColumnJson("ExtLineKey", 450473611, "Code[20]")} ]");
        var hdr = DataItemJson("ExtHdr", "Src", 1, 378700002, table,
            properties: """
                [ { "Name": "DataItemLink", "Value": "\"Entry No.\" = field(\"Entry No.\")" },
                  { "Name": "DataItemTableView", "Value": "sorting(\"Entry No.\") where(\"Entry No.\" = filter(<> 0))" } ]
                """,
            columns: $"[ {ColumnJson("ExtHdrNo", 1439463707, "Integer")}, {ColumnJson("ExtHdrDesc", 914509453, "Text[50]")}, {ColumnJson("ExtHdrComputed", 1394413973, "String")} ]",
            children: $"[ {line} ]");
        var first = DataItemJson("ExtFirst", "Src", 1, 1678737158, table, columns: $"[ {ColumnJson("ExtFirstNo", 257582913, "Integer")} ]");
        var after = DataItemJson("ExtAfter", "Src", 1, 533819344, table, columns: $"[ {ColumnJson("ExtAfterNo", 1618849833, "Integer")} ]");
        var before = DataItemJson("ExtBefore", "Src", 1, 73650211, table, columns: $"[ {ColumnJson("ExtBeforeNo", 2012721092, "Integer")} ]");
        return $"[ {hdr}, {first}, {after}, {before} ]";
    }

    private static List<string> DataItemAdds(string xml)
    {
        var ns = XNamespace.Get("urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");
        string Text(XElement e, string name) => e.Element(ns + name)?.Value ?? "";
        return XDocument.Parse(xml).Root!.Elements(ns + "DataItemAdd").Select(add =>
        {
            var item = add.Element(ns + "DataItem")!;
            var fields = string.Join(";", item.Elements(ns + "DataItemField").Select(f =>
                $"{Text(f, "ID")}/{Text(f, "FriendlyFieldName")}/{Text(f, "FieldType")}/{Text(f, "FieldNo")}/{Text(f, "SourceExpr")}"));
            return $"{Text(add, "AnchorName")}|{Text(add, "AnchorId")}|{Text(add, "Operation")}|{Text(item, "DataItemIndent")}"
                + $"|{Text(item, "ID")}|{Text(item, "DataItemVarName")}|{Text(item, "DataItemTable")}|{fields}";
        }).ToList();
    }

    [Fact]
    public void TheDerivedDocument_StatesWhatBcsEmitterStatesForTheSameExtension()
    {
        // Ids as BC's emitter assigned them in the captured document: report 90411, extension 90412,
        // table 90410. The fixture's names are rewritten to this run's, so only the ids need to match.
        var f = new Fixture(90411, 90412, "ZZ Base", "ZZ Ext", "PRX Sample");
        var source = ExtensionSource(f, ZzDataset);
        WithApp(f, SymbolReference(f, 90410, ZzExtensionDataItems(f.TableName)), source, _ =>
        {
            var derived = Delta(f, out var reason);
            Assert.True(derived != null, $"a document was expected; the reason given: {reason}");
            var expected = DataItemAdds(BcEmittedDocument);
            var actual = DataItemAdds(derived!);
            Assert.Equal(expected.Count, actual.Count);
            for (var i = 0; i < expected.Count; i++) Assert.Equal(expected[i], actual[i]);
        });
    }

    [Fact]
    public void SeveralDataItemsInOneAddBlock_ChainEachAfterThePreviousOne_AsBcsEmitterDoes()
    {
        var f = new Fixture(90411, 90412, "ZZ Base", "ZZ Ext", "PRX Sample");
        string Item(string name, int id) => DataItemJson(name, "Src", 1, id, f.TableName);
        var items = "[ " + string.Join(", ", Item("XA", 504038852), Item("XB", 1877838593), Item("XC", 1779432086),
            Item("YA", 434355077), Item("YB", 1072051694)) + " ]";
        var source = ExtensionSource(f, """
                addbefore(Child2)
                {
                    dataitem(XA; "PRX Sample") { }
                    dataitem(XB; "PRX Sample") { }
                    dataitem(XC; "PRX Sample") { }
                }
                addafter(Child1)
                {
                    dataitem(YA; "PRX Sample") { }
                    dataitem(YB; "PRX Sample") { }
                }
        """);
        WithApp(f, SymbolReference(f, 90410, items), source, _ =>
        {
            var derived = Delta(f, out var reason);
            Assert.True(derived != null, $"a document was expected; the reason given: {reason}");
            var expected = DataItemAdds(BcEmittedMultiRootDocument);
            var actual = DataItemAdds(derived!);
            Assert.Equal(expected.Count, actual.Count);
            for (var i = 0; i < expected.Count; i++) Assert.Equal(expected[i], actual[i]);

            var merged = NavReportSync.ApplyReportExtensionDeltas(f.ReportId, BaseMetaReport(f));
            Assert.Equal(new[] { "Src", "Child1", "YA", "YB", "XA", "XB", "XC", "Child2" }, Order(merged));
        });
    }

    // ── each add keyword reaches BC's applicator as its own operation ─────────────────────────

    private static Microsoft.Dynamics.Nav.Types.Metadata.MetaReport BaseMetaReport(Fixture f)
    {
        var xml = RecordPatches.TryBuildDependencyReportMetadata(f.ReportId)
            ?? throw new InvalidOperationException("the fixture report has no document");
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(xml);
        var ctor = typeof(Microsoft.Dynamics.Nav.Types.Metadata.MetaReport)
            .GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Single(c => c.GetParameters() is { Length: >= 3 } ps && typeof(System.Xml.XmlNode).IsAssignableFrom(ps[0].ParameterType));
        var args = ctor.GetParameters()
            .Select((p, i) => i == 0 ? doc.DocumentElement : p.ParameterType == typeof(int) ? (object?)0 : null).ToArray();
        return (Microsoft.Dynamics.Nav.Types.Metadata.MetaReport)ctor.Invoke(args);
    }

    private static string[] Order(object metaReport)
        => ((Microsoft.Dynamics.Nav.Types.Metadata.MetaReport)metaReport).DataItems.Select(d => d.DataItemVarName).ToArray();

    [Theory]
    [InlineData("addlast(Src)", new[] { "Src", "Child1", "Child2", "ExtOne" })]
    [InlineData("addfirst(Src)", new[] { "Src", "ExtOne", "Child1", "Child2" })]
    [InlineData("addafter(Child1)", new[] { "Src", "Child1", "ExtOne", "Child2" })]
    [InlineData("addbefore(Child2)", new[] { "Src", "Child1", "ExtOne", "Child2" })]
    public void AnAddKeyword_PlacesTheDataItemWhereBcsApplicatorPutsIt(string statement, string[] expectedOrder)
    {
        var f = NewFixture();
        var dataItems = "[ " + DataItemJson("ExtOne", "Src", 1, 111, f.TableName) + " ]";
        var source = ExtensionSource(f, $$"""        {{statement}} { dataitem(ExtOne; "{{f.TableName}}") { } }""");
        WithApp(f, SymbolReference(f, f.ReportId + 100, dataItems), source, _ =>
        {
            var merged = NavReportSync.ApplyReportExtensionDeltas(f.ReportId, BaseMetaReport(f));
            Assert.Equal(expectedOrder, Order(merged));
            Assert.Equal(new[] { f.ExtensionId }, NavReportSync.MergedReportExtensionsOf(merged));
        });
    }

    [Fact]
    public void ANestedAddedDataItem_FollowsItsParent_AtTheIndentTheSymbolFileStates()
    {
        var f = NewFixture();
        var child = DataItemJson("ExtChild", "ExtOne", 2, 222, f.TableName);
        var dataItems = "[ " + DataItemJson("ExtOne", "Src", 1, 111, f.TableName, children: $"[ {child} ]") + " ]";
        var source = ExtensionSource(f, $$"""        addafter(Child1) { dataitem(ExtOne; "{{f.TableName}}") { dataitem(ExtChild; "{{f.TableName}}") { } } }""");
        WithApp(f, SymbolReference(f, f.ReportId + 100, dataItems), source, _ =>
        {
            var merged = (Microsoft.Dynamics.Nav.Types.Metadata.MetaReport)NavReportSync.ApplyReportExtensionDeltas(f.ReportId, BaseMetaReport(f));
            Assert.Equal(new[] { "Src", "Child1", "ExtOne", "ExtChild", "Child2" }, Order(merged));
            Assert.Equal(new[] { 0, 1, 1, 2, 1 }, merged.DataItems.Select(d => (int)d.DataItemIndent).ToArray());
        });
    }

    // ── what stays unmerged, with the reason ──────────────────────────────────────────────────

    public static IEnumerable<object?[]> Unmergeable()
    {
        yield return new object?[] { "NoSource", "ships no source", false };
        yield return new object?[] { "AnchorNotInReport", "anchor 'Nowhere'", true };
        yield return new object?[] { "AddsAColumn", "adds columns to an existing data item", true };
        yield return new object?[] { "ColumnsAtExtensionLevel", "adds columns to an existing data item", true };
        yield return new object?[] { "TableDoesNotResolve", "does not resolve", true };
        yield return new object?[] { "NoDataItems", "no added data item", true };
    }

    [Theory]
    [MemberData(nameof(Unmergeable))]
    public void WhatCannotBeStatedWhole_IsLeftUnmerged_WithItsReason(string kind, string reasonFragment, bool withSource)
    {
        var f = NewFixture();
        var one = DataItemJson("ExtOne", "Src", 1, 111, f.TableName);
        var items = "[ " + one + " ]";
        var extColumns = "[]";
        var statement = $$"""        addafter(Child1) { dataitem(ExtOne; "{{f.TableName}}") { } }""";
        switch (kind)
        {
            case "AnchorNotInReport": statement = $$"""        addafter(Nowhere) { dataitem(ExtOne; "{{f.TableName}}") { } }"""; break;
            case "AddsAColumn": statement += $$"""{{Environment.NewLine}}        add(Child1) { column(Added; Description) { } }"""; break;
            case "ColumnsAtExtensionLevel": extColumns = "[ " + ColumnJson("Added", 5, "Text[50]") + " ]"; break;
            case "TableDoesNotResolve": items = "[ " + DataItemJson("ExtOne", "Src", 1, 111, "No Such Table") + " ]"; break;
            case "NoDataItems": items = "[]"; break;
        }
        var source = withSource ? ExtensionSource(f, statement) : null;
        WithApp(f, SymbolReference(f, f.ReportId + 100, items, extColumns), source, _ =>
        {
            Assert.Null(Delta(f, out var reason));
            Assert.Contains(reasonFragment, reason, StringComparison.Ordinal);
            Assert.Empty(RecordPatches.PrecompiledReportExtensionDeltasFor(f.ReportId));
        });
    }

    [Fact]
    public void OnlyTheExtensionsWhoseItemsCanBeStated_GetADocument()
    {
        var f = NewFixture();
        var items = "[ " + DataItemJson("ExtOne", "Src", 1, 111, f.TableName) + " ]";
        var source = ExtensionSource(f, $$"""        addafter(Child1) { dataitem(ExtOne; "{{f.TableName}}") { } }""");
        WithApp(f, SymbolReference(f, f.ReportId + 100, items), source, _ =>
        {
            var documents = RecordPatches.PrecompiledReportExtensionDeltasFor(f.ReportId);
            Assert.Equal(new[] { f.ExtensionId }, documents.Select(d => d.ExtensionId));
            Assert.Contains("<Operation>AddAfter</Operation>", documents[0].Xml, StringComparison.Ordinal);
            Assert.Empty(RecordPatches.PrecompiledReportExtensionDeltasFor(f.ReportId + 1));
        });
    }

    // A precompiled document BC's parser takes badly must not take the report's metadata with it:
    // that is what opening the request page needs. The extension stays unmerged, and a
    // source-compiled one beside it is still applied.
    [Fact]
    public void APrecompiledDocumentBcRejects_LeavesThatExtensionUnmerged_AndTheReportItsMetadata()
    {
        var f = NewFixture();
        var items = "[ " + DataItemJson("ExtOne", "Src", 1, 111, f.TableName) + " ]";
        WithApp(f, SymbolReference(f, f.ReportId + 100, items), source: null, _ =>
        {
            var good = (f.ExtensionId, Xml: """
                <ReportExtension xmlns="urn:schemas-microsoft-com:dynamics:NAV:MetaObjects" ALNamespace="">
                  <MetadataVersion>130000</MetadataVersion><ID>1</ID><Name>Good</Name>
                  <DataItemAdd><AnchorName>Src</AnchorName><AnchorId>1369927887</AnchorId><Operation>AddLast</Operation>
                    <DataItem><DataItemTable>0</DataItemTable><DataItemIndent>1</DataItemIndent><ID>5</ID><DataItemVarName>FromSource</DataItemVarName></DataItem>
                  </DataItemAdd><Labels /><Layouts />
                </ReportExtension>
                """);
            var bad = (ExtensionId: f.ExtensionId + 1, Xml: """
                <ReportExtension xmlns="urn:schemas-microsoft-com:dynamics:NAV:MetaObjects" ALNamespace="">
                  <MetadataVersion>130000</MetadataVersion><ID>2</ID><Name>Bad</Name>
                  <DataItemAdd><AnchorName>Nowhere</AnchorName><AnchorId>5</AnchorId><Operation>AddLast</Operation>
                    <DataItem><DataItemTable>0</DataItemTable><DataItemIndent>1</DataItemIndent><ID>6</ID><DataItemVarName>NeverMerged</DataItemVarName></DataItem>
                  </DataItemAdd><Labels /><Layouts />
                </ReportExtension>
                """);
            var merged = NavReportSync.ApplyReportExtensionDeltas(f.ReportId, BaseMetaReport(f),
                precompiled: new[] { bad }, source: new[] { good });
            Assert.Equal(new[] { f.ExtensionId }, NavReportSync.MergedReportExtensionsOf(merged));
            Assert.Contains("FromSource", Order(merged));
        });
    }
}
