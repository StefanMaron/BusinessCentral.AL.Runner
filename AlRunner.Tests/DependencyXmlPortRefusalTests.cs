// A precompiled dependency xmlport that the projection REFUSES must be reported as refused,
// with the projection's reason, and never as "no loaded dependency .app declares it" (#4650).
// And a namespace-qualified tabledata name in its Permissions is not a reason to refuse: BC
// writes the last segment only (docs/xmlport-metadata-from-bc.md#table-views-link-fields-and-permissions).
//
// Synthetic .apps (SymbolReference.json + the xmlport's AL source), so no Base Application
// floor (.claude/rules/no-base-app-in-csharp-tests.md).
using System.IO.Compression;
using System.Text;
using System.Xml;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public sealed class DependencyXmlPortRefusalTests : IDisposable
{
    // Process-wide unique: _bcAppPaths and the xmlport memo are process-global.
    private const int RefusedPortId = 88124650;
    private const int NamespacedPortId = 88124651;
    private const int UndeclaredPortId = 88124652;

    private readonly string _root = TestScratch.Dir("al-runner-4650-xmlport-refusal");

    public DependencyXmlPortRefusalTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private string WriteApp(int id, string name, string permissions)
    {
        var src = $"src/XPR{id}.XmlPort.al";
        var symbols = $$"""
            { "RuntimeVersion": "15.1", "XmlPorts": [ {
                "Id": {{id}}, "Name": "{{name}}",
                "Properties": [ { "Name": "Permissions", "Value": {{System.Text.Json.JsonSerializer.Serialize(permissions)}} } ],
                "ReferenceSourceFileName": "{{src}}" } ] }
            """;
        var al = $$"""
            xmlport {{id}} "{{name}}"
            {
                Permissions = {{permissions}};
                schema
                {
                    textelement(Root) { }
                }
            }
            """;
        var appPath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".app");
        using (var fs = new FileStream(appPath, FileMode.Create))
        using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var (entryName, content) in new[] { ("SymbolReference.json", symbols), (src, al) })
            {
                using var w = new StreamWriter(za.CreateEntry(entryName).Open(), Encoding.UTF8);
                w.Write(content);
            }
        }
        return appPath;
    }

    private static NCLObjectXmlMetadata Load(int id)
        => new RunnerXmlMetadataLoader().GetMetaObjectXmlMetadata(
            new ApplicationObjectId(ObjectType.XmlPort, id), appGroup: null!);

    /// <summary>
    /// Base Application xmlport 5801 states
    /// <c>tabledata Microsoft.Manufacturing.Capacity."Capacity Ledger Entry" = rimd</c>.
    /// </summary>
    [Fact]
    public void NamespaceQualifiedTableData_IsWrittenAsItsLastSegment()
    {
        RecordPatches.AddBcAppPath(WriteApp(NamespacedPortId, "XPR Namespaced",
            "tabledata \"XPR Plain\" = RIMD, tabledata Probe.Alpha.Data.\"NS Cap Entry\" = rimd, "
            + "tabledata Probe.Alpha.Data.NSCapNoQuote = r"));

        var xml = RecordPatches.TryBuildDependencyXmlPortMetadata(NamespacedPortId);
        Assert.NotNull(xml);
        var doc = new XmlDocument();
        doc.LoadXml(xml!);
        Assert.Equal("TableData XPR Plain=rimd,TableData NS Cap Entry=rimd,TableData NSCapNoQuote=r",
            doc.SelectSingleNode("/XmlPort/Permissions")?.InnerText);
        Assert.Null(RecordPatches.DependencyXmlPortRefusal(NamespacedPortId));
    }

    [Fact]
    public void RefusedXmlPort_IsReportedWithTheProjectionsReason()
    {
        // `Q` is not a permission letter, so the encoder has no canonical form for it.
        RecordPatches.AddBcAppPath(WriteApp(RefusedPortId, "XPR Refused", "tabledata \"XPR Plain\" = Q"));

        Assert.Null(RecordPatches.TryBuildDependencyXmlPortMetadata(RefusedPortId));
        var ex = Assert.Throws<RunnerOutOfScopeException>(() => Load(RefusedPortId));
        Assert.Contains("declares this xmlport", ex.Message);
        Assert.Contains("Permissions on 'XPR Refused'", ex.Message);
        Assert.Contains("tabledata \"XPR Plain\" = Q", ex.Message);
        Assert.DoesNotContain("no loaded dependency .app declares it", ex.Message);
    }

    [Fact]
    public void UndeclaredXmlPort_KeepsTheNotDeclaredReason()
    {
        Assert.Null(RecordPatches.DependencyXmlPortRefusal(UndeclaredPortId));
        var ex = Assert.Throws<RunnerOutOfScopeException>(() => Load(UndeclaredPortId));
        Assert.Contains("no loaded dependency .app declares it", ex.Message);
    }
}
