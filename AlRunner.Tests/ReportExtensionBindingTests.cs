// Issues #4909 / #4918: each reportextension of a report is bound through BC's own
// NavReport.RegisterReportExtension at construction once its runtime deltas are merged into the
// report's MetaReport (NavReportSync.ApplyReportExtensionDeltas, BC's MetadataRuntimeDeltaApplicator).
// These pin which extensions the runner finds for a report, that one whose compiled type is not
// loaded refuses, the classification that decides whether an unmerged (precompiled, #4837)
// extension can still be bound whole, and the delta merge itself — including that a delta BC's
// applicator cannot apply refuses rather than leaving the report without part of the extension.
// The end-to-end claims are the corpus's: codeunits 67546, 67547 (request page) and 68001
// (report triggers, added data items and columns). The synthetic NavReportExtension subclasses are
// shaped like compiler output, including the overrides Base App's precompiled 99000783 carries
// (IsCompiledForOnPremise, __IsAsync) that declare nothing.
using System.IO.Compression;
using System.Text;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Runtime.Extensions;
using Microsoft.Dynamics.Nav.Types;
using Xunit;

namespace AlRunner.Tests;

[Collection(CacheRootsSerialCollection.Name)]
public class ReportExtensionBindingTests
{
    private const string SymbolReference = """
        {
          "RuntimeVersion": "17.0",
          "Reports": [
            { "Id": 88490901, "Name": "RXB Report", "RequestPage": { "Id": 0, "Name": "RequestOptionsPage" } },
            { "Id": 88490902, "Name": "RXB Plain Report", "RequestPage": { "Id": 0, "Name": "RequestOptionsPage" } }
          ],
          "ReportExtensions": [
            { "Id": 88490911, "Name": "RXB Report Ext", "Target": "RXB Report",
              "RequestPage": { "ControlChanges": [] } }
          ]
        }
        """;

    private static void WithDependencyApp(Action body)
    {
        var dir = TestScratch.Dir("al-runner-report-extension-binding-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".app");
            using (var zip = new FileStream(appPath, FileMode.Create))
            using (var za = new ZipArchive(zip, ZipArchiveMode.Create))
            using (var w = new StreamWriter(za.CreateEntry("SymbolReference.json").Open(), Encoding.UTF8))
                w.Write(SymbolReference);
            RecordPatches.AddBcAppPath(appPath);
            body();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void APrecompiledReportExtension_IsFoundForItsTargetReport_AndForNoOther()
        => WithDependencyApp(() =>
        {
            Assert.Equal(new[] { 88490911 }, RecordPatches.ReportExtensionIdsFor(88490901));
            Assert.Empty(RecordPatches.ReportExtensionIdsFor(88490902));
        });

    [Fact]
    public void AReportExtensionWhoseCompiledTypeIsNotLoaded_Refuses_RatherThanStayingUnbound()
        => WithDependencyApp(() =>
        {
            var ex = Assert.Throws<RunnerOutOfScopeException>(() => NavReportSync.ResolveReportExtensionTypes(88490901));
            Assert.Contains("reportextension 88490911", ex.Message, StringComparison.Ordinal);
            Assert.Contains("ReportExtension88490911", ex.Message, StringComparison.Ordinal);
            Assert.Empty(NavReportSync.ResolveReportExtensionTypes(88490902));
        });

    // Precompiled Base App shape (99000783, measured on bc28): overrides that declare no report behaviour.
    private sealed class NothingDeclaredExt : NavReportExtension
    {
        public NothingDeclaredExt(ITreeObject parent) : base(parent, 1) { }
        public override string ObjectName => "Nothing";
        public override bool IsCompiledForOnPremise => true;
        public override bool __IsAsync => false;
        public override void OnClear() { }
        public override void RegisterDataItems(NavReport report) { }
    }

    private sealed class PreReportExt : NavReportExtension
    {
        public PreReportExt(ITreeObject parent) : base(parent, 2) { }
        public override void OnPreReport() { }
    }

    private sealed class PostReportExt : NavReportExtension
    {
        public PostReportExt(ITreeObject parent) : base(parent, 3) { }
        public override void OnPostReport() { }
    }

    private sealed class AddedColumnExt : NavReportExtension
    {
        public AddedColumnExt(ITreeObject parent) : base(parent, 4) { }
        public override NavValue EvaluateSourceExpression(string fieldName) => null!;
    }

    private sealed class AddedDataItemExt : NavReportExtension
    {
        public AddedDataItemExt(ITreeObject parent) : base(parent, 5) { }
        public override void RegisterDataItems(NavReport report) => GC.KeepAlive(report);
    }

    [Fact]
    public void AnExtensionDeclaringNoReportBehaviour_IsNotRefused_EvenWithCompilerOverrides()
        => Assert.False(NavReportSync.DeclaresReportBehaviour(typeof(NothingDeclaredExt)));

    [Theory]
    [InlineData(typeof(PreReportExt))]
    [InlineData(typeof(PostReportExt))]
    [InlineData(typeof(AddedColumnExt))]
    [InlineData(typeof(AddedDataItemExt))]
    public void AnExtensionDeclaringReportBehaviour_IsRefused(Type extensionType)
        => Assert.True(NavReportSync.DeclaresReportBehaviour(extensionType));

    // Report triggers alone need nothing in the MetaReport, so an unmerged extension declaring only
    // them is still bound whole; an added column or data item needs its metadata (#4837).
    [Theory]
    [InlineData(typeof(NothingDeclaredExt), false)]
    [InlineData(typeof(PreReportExt), false)]
    [InlineData(typeof(PostReportExt), false)]
    [InlineData(typeof(AddedColumnExt), true)]
    [InlineData(typeof(AddedDataItemExt), true)]
    public void OnlyAnAddedColumnOrDataItem_AddsToTheDataset(Type extensionType, bool expected)
        => Assert.Equal(expected, NavReportSync.AddsToDataset(extensionType));

    // The shape BC's compiler emits: a report document, and a reportextension's own document
    // (captured from corpus report 68001 / reportextension 68001, request page left out).
    private const string ReportXml = """
        <Report Extensible="1">
          <ProcessingOnly>0</ProcessingOnly>
          <MetadataVersion>130000</MetadataVersion>
          <ID>88490921</ID>
          <Name>RXB Data Report</Name>
          <DataItem>
            <MaxIteration>0</MaxIteration>
            <PrintOnlyIfDetail>0</PrintOnlyIfDetail>
            <DataItemTable>2000000026</DataItemTable>
            <DataItemIndent>0</DataItemIndent>
            <ID>1248146253</ID>
            <DataItemVarName>DataBaseItem</DataItemVarName>
            <FieldReferences>1</FieldReferences>
            <DataItemField>
              <AutoCalcField>1</AutoCalcField>
              <ID>2071979254</ID>
              <FriendlyFieldName>BaseTag</FriendlyFieldName>
              <FieldType>String</FieldType>
              <FieldNo>-1</FieldNo>
              <SourceExpr>Format(Number)</SourceExpr>
            </DataItemField>
          </DataItem>
        </Report>
        """;

    private static string ExtensionXml(string anchor, int anchorId) => $"""
        <ReportExtension xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema" ALNamespace="" xmlns="urn:schemas-microsoft-com:dynamics:NAV:MetaObjects">
          <MetadataVersion>130000</MetadataVersion>
          <ID>88490922</ID>
          <Name>RXB Data Report Ext</Name>
          <ColumnAdd>
            <AnchorName>{anchor}</AnchorName>
            <AnchorId>{anchorId}</AnchorId>
            <FieldReferences>1</FieldReferences>
            <DataItemField>
              <AutoCalcField>1</AutoCalcField>
              <ID>652273881</ID>
              <FriendlyFieldName>AddedTag</FriendlyFieldName>
              <FieldType>String</FieldType>
              <FieldNo>-1</FieldNo>
              <SourceExpr>Format(DataBaseItem.Number)</SourceExpr>
            </DataItemField>
          </ColumnAdd>
          <DataItemAdd>
            <AnchorName>{anchor}</AnchorName>
            <AnchorId>{anchorId}</AnchorId>
            <Operation>AddLast</Operation>
            <DataItem>
              <MaxIteration>0</MaxIteration>
              <PrintOnlyIfDetail>0</PrintOnlyIfDetail>
              <DataItemTable>2000000026</DataItemTable>
              <DataItemIndent>1</DataItemIndent>
              <ID>1971863633</ID>
              <DataItemVarName>DataExtItem</DataItemVarName>
              <DataItemLinkReference>{anchor}</DataItemLinkReference>
            </DataItem>
          </DataItemAdd>
          <Labels />
          <Layouts />
          <Triggers>
            <Trigger TriggerType="OnPreReport" Name="OnPreReport" />
          </Triggers>
        </ReportExtension>
        """;

    private static Microsoft.Dynamics.Nav.Types.Metadata.MetaReport BuildMetaReport()
    {
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(ReportXml);
        var ctor = typeof(Microsoft.Dynamics.Nav.Types.Metadata.MetaReport)
            .GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Single(c => c.GetParameters() is { Length: >= 3 } ps && typeof(System.Xml.XmlNode).IsAssignableFrom(ps[0].ParameterType));
        var args = ctor.GetParameters()
            .Select((p, i) => i == 0 ? doc.DocumentElement : p.ParameterType == typeof(int) ? (object?)0 : null).ToArray();
        return (Microsoft.Dynamics.Nav.Types.Metadata.MetaReport)ctor.Invoke(args);
    }

    private static IEnumerable<string> ColumnNames(Microsoft.Dynamics.Nav.Types.Metadata.MetaDataItem dataItem)
        => dataItem.DataItemColumns.Select(c => c.Name);

    [Fact]
    public void ApplyingAReportExtensionsDeltas_AddsItsDataItemAndColumn_ThroughBcsApplicator()
    {
        var baseMeta = BuildMetaReport();
        Assert.Throws<ArgumentException>(() => baseMeta.GetDataItemByName("DataExtItem"));

        var merged = (Microsoft.Dynamics.Nav.Types.Metadata.MetaReport)NavReportSync.ApplyReportExtensionDeltas(
            88490921, baseMeta, new[] { (88490922, ExtensionXml("DataBaseItem", 1248146253)) });

        Assert.Equal("DataExtItem", merged.GetDataItemByName("DataExtItem").DataItemVarName);
        Assert.Equal(new[] { "BaseTag", "AddedTag" }, ColumnNames(merged.GetDataItemByName("DataBaseItem")));
        Assert.Equal(new[] { 88490922 }, NavReportSync.MergedReportExtensionsOf(merged));
    }

    [Fact]
    public void ADeltaBcsApplicatorCannotApply_Refuses_RatherThanDroppingPartOfTheExtension()
    {
        var ex = Assert.Throws<RunnerOutOfScopeException>(() => NavReportSync.ApplyReportExtensionDeltas(
            88490921, BuildMetaReport(), new[] { (88490922, ExtensionXml("NoSuchDataItem", 7)) }));
        Assert.Contains("report 88490921 with reportextension 88490922", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoDeltaDocuments_LeaveTheMetaReportAsItWas()
    {
        var baseMeta = BuildMetaReport();
        Assert.Same(baseMeta, NavReportSync.ApplyReportExtensionDeltas(88490921, baseMeta, Array.Empty<(int, string)>()));
        Assert.Empty(NavReportSync.MergedReportExtensionsOf(baseMeta));
    }
}
