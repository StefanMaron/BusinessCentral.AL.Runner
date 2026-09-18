// DependencyPageMethodSubtreeRenderingParityTests — the page and codeunit <Methods> renderers
// must produce the SAME element shape, because BC's ObjectMetadataEmitter writes one (#4267).
//
// WHY THIS EXISTS AS A TEST RATHER THAN AS A SHARED FUNCTION
//   The two renderers cannot literally be one function: the codeunit path builds an XmlDocument
//   (RecordPatches.CodeunitMetadataEquivalence.cs, AppendMethodsSubtree) and the page path writes
//   through an XmlWriter (DependencyPageMetadataXml.cs, WriteMethodsSubtree), because each is
//   embedded in a document its own emitter already had reasons to build that way. The FILTER and
//   the READER are shared — EmittedMethodAttributeKinds and ReadAttributedMethods — so what is
//   left to drift is exactly the element shape, and that is what this pins.
//
//   Without it, the drift is silent in the direction that matters: BC's own reader
//   (MetaCodeunit(XmlNode)) throws NullReferenceException on an attribute element with no Name,
//   and MetadataObjectDiff would report the page difference as a <Methods> content mismatch that
//   reads like a data problem rather than a rendering one.
//
// WHAT IT DOES
//   One fixture .app declares a codeunit and a page whose Methods arrays are IDENTICAL — same
//   ids, same names, same attribute kinds, same positional arguments. Both witnesses are
//   registered as clear. The two documents are then rendered through the real entry points and
//   their <Methods> subtrees canonicalised and compared.
//
//   The fixture deliberately spans all three shapes BC emits: IntegrationEvent (IncludeSender in
//   slot 0, Isolated in slot 2), InternalEvent (no sender argument at all, Isolated in slot 1),
//   and InherentPermissions, which maps to a different MethodAttributes child element entirely.

using System.IO.Compression;
using System.Text;
using System.Xml;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// The codeunit half reaches the RecordPatches parse statics, which are process-wide and which
// xunit's parallel collections can clear between a write and a read (#1696, #1712) — the same
// collection CodeunitMethodSubtreeDerivationTests joins for that reason.
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class DependencyPageMethodSubtreeRenderingParityTests : IDisposable
{
    private const string MetaNs = "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects";

    private const int ParityCodeunitId = 88267101;
    private const int ParityPageId = 88267102;

    /// <summary>
    /// The identical method array on both objects. Ids and names are arbitrary but DISTINCT from
    /// each other, so a renderer that dropped one, reordered two, or wrote the wrong slot's flag
    /// produces a different canonical string rather than an accidentally equal one.
    /// </summary>
    private const string SymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Codeunits": [
            {
              "Id": 88267101,
              "Name": "P4267 Parity Codeunit",
              "Properties": [],
              "Methods": [
                { "Id": 11, "Name": "PlainProcedure", "Attributes": [] },
                { "Id": 2001, "Name": "OnIntegrationSenderIsolated",
                  "Attributes": [ { "Name": "IntegrationEvent", "Arguments": [
                    { "Value": "True" }, { "Value": "False" }, { "Value": "True" } ] } ] },
                { "Id": 2002, "Name": "OnInternalIsolated",
                  "Attributes": [ { "Name": "InternalEvent", "Arguments": [
                    { "Value": "False" }, { "Value": "True" } ] } ] },
                { "Id": 2003, "Name": "OnBusinessPlain",
                  "Attributes": [ { "Name": "BusinessEvent", "Arguments": [
                    { "Value": "False" }, { "Value": "False" } ] } ] },
                { "Id": 2004, "Name": "GuardedProcedure",
                  "Attributes": [ { "Name": "InherentPermissions" } ] }
              ]
            }
          ],
          "Pages": [
            {
              "Id": 88267102,
              "Name": "P4267 Parity Page",
              "Properties": [ { "Name": "PageType", "Value": "List" } ],
              "Methods": [
                { "Id": 11, "Name": "PlainProcedure", "Attributes": [] },
                { "Id": 2001, "Name": "OnIntegrationSenderIsolated",
                  "Attributes": [ { "Name": "IntegrationEvent", "Arguments": [
                    { "Value": "True" }, { "Value": "False" }, { "Value": "True" } ] } ] },
                { "Id": 2002, "Name": "OnInternalIsolated",
                  "Attributes": [ { "Name": "InternalEvent", "Arguments": [
                    { "Value": "False" }, { "Value": "True" } ] } ] },
                { "Id": 2003, "Name": "OnBusinessPlain",
                  "Attributes": [ { "Name": "BusinessEvent", "Arguments": [
                    { "Value": "False" }, { "Value": "False" } ] } ] },
                { "Id": 2004, "Name": "GuardedProcedure",
                  "Attributes": [ { "Name": "InherentPermissions" } ] }
              ]
            }
          ]
        }
        """;

    private readonly string _dir;

    public DependencyPageMethodSubtreeRenderingParityTests()
    {
        _dir = TestScratch.Dir("al-runner-page-method-parity-4267");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        RecordPatches.ResetForReload();
        RecordPatches.ClearCodeunitSubscriberWitnessForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void Register()
    {
        var appPath = Path.Combine(_dir, "parity.app");
        using (var zip = new FileStream(appPath, FileMode.Create))
        using (var za = new ZipArchive(zip, ZipArchiveMode.Create))
        {
            var entry = za.CreateEntry("SymbolReference.json");
            using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
            w.Write(SymbolReference);
        }

        RecordPatches.ResetForReload();
        RecordPatches.ClearCodeunitSubscriberWitnessForTests();
        RecordPatches.AddBcAppPath(appPath);

        RecordPatches.RegisterCodeunitSubscriberWitness(
            appPath, Array.Empty<int>(), new[] { ParityCodeunitId });
        RecordPatches.RegisterPageSubscriberWitness(
            appPath, Array.Empty<int>(), new[] { ParityPageId });
    }

    /// <summary>
    /// A stable rendering of one element tree: local name, namespace, attributes sorted by name,
    /// then children in DOCUMENT order. Attribute order is normalised because BC's reader does
    /// not depend on it; child order is not, because <c>MetadataObjectDiff</c> pairs
    /// <c>Methods</c> positionally.
    /// </summary>
    private static string Canonical(XmlElement element, int depth = 0)
    {
        var sb = new StringBuilder();
        sb.Append(new string(' ', depth * 2))
          .Append('{').Append(element.NamespaceURI).Append('}').Append(element.LocalName);
        foreach (var name in element.Attributes.OfType<XmlAttribute>()
                     .Where(a => a.Prefix != "xmlns" && a.Name != "xmlns")
                     .Select(a => a.LocalName).OrderBy(n => n, StringComparer.Ordinal))
            sb.Append(' ').Append(name).Append("=\"").Append(element.GetAttribute(name)).Append('"');
        sb.Append('\n');
        foreach (var child in element.ChildNodes.OfType<XmlElement>())
            sb.Append(Canonical(child, depth + 1));
        return sb.ToString();
    }

    private static XmlElement MethodsOf(string xml, string what)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        var methods = doc.DocumentElement!
            .GetElementsByTagName("Methods", MetaNs).OfType<XmlElement>().FirstOrDefault();
        Assert.True(methods is not null, $"the {what} document carries no <Methods> element");
        return methods!;
    }

    /// <summary>
    /// The parity assertion. Both renderers are driven through their real entry points, so this
    /// fails if either one changes the element name, the attribute set, a value's spelling
    /// (<c>"True"</c> vs <c>"true"</c>), the positional-flag mapping, or the method order.
    /// </summary>
    [Fact]
    public void PageAndCodeunitRenderers_ProduceTheSameMethodsSubtree()
    {
        Register();

        var codeunitXml = RecordPatches.TryBuildCodeunitMetadataEquivalenceXml(ParityCodeunitId);
        Assert.True(codeunitXml is not null, "the runner derived no metadata for the parity codeunit");
        var pageXml = RecordPatches.TryBuildDependencyPageMetadata(ParityPageId);
        Assert.True(pageXml is not null, "the runner derived no metadata for the parity page");

        Assert.Equal(
            Canonical(MethodsOf(codeunitXml!, "codeunit")),
            Canonical(MethodsOf(pageXml!, "page")));
    }

    /// <summary>
    /// The parity assertion above is worth nothing if both sides render an empty subtree, so this
    /// pins what they agree ON: four of the five declared methods, in the symbol file's order,
    /// with the plain procedure filtered out and <c>InherentPermissions</c> mapped to its own
    /// attribute element.
    /// </summary>
    [Fact]
    public void TheAgreedSubtree_CarriesTheFourAttributedMethods_InDocumentOrder()
    {
        Register();

        var methods = MethodsOf(
            RecordPatches.TryBuildDependencyPageMetadata(ParityPageId)!, "page");

        var rendered = methods.ChildNodes.OfType<XmlElement>()
            .Select(m => (
                Id: m.GetAttribute("ID"),
                Name: m.GetAttribute("Name"),
                Kind: m.ChildNodes.OfType<XmlElement>()
                    .First(e => e.LocalName == "MethodAttributes")
                    .ChildNodes.OfType<XmlElement>().First().LocalName))
            .ToList();

        Assert.Equal(
            new List<(string, string, string)>
            {
                ("2001", "OnIntegrationSenderIsolated", "EventPublisherAttribute"),
                ("2002", "OnInternalIsolated", "EventPublisherAttribute"),
                ("2003", "OnBusinessPlain", "EventPublisherAttribute"),
                ("2004", "GuardedProcedure", "InherentPermissionsMethodAttribute"),
            },
            rendered);
    }
}
