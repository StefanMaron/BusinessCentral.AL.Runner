// DependencyPagePartProviderLinkTests — pins the runner's own mechanism for issue #5140: a part
// of a PRECOMPILED dependency page that declares `Provider = <sibling part>` links its FIELD
// SubPageLink entries from the PROVIDER part's source table, not the host page's, and the
// reconstructed InfopartPageDefinition carries ProviderID so the live TestPage part can read the
// provider's row. The BC-observable claim (a provider-linked FactBox shows the rows of the
// provider's current row) is measured upstream in the corpus (codeunit 69140 "PVD Provider Tests").

using System.IO.Compression;
using System.Text;
using System.Xml;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(CacheRootsSerialCollection.Name)]
public class DependencyPagePartProviderLinkTests
{
    private const int HostPageId = 88124501;
    private const int LinesControlId = 88124599;
    private const int FactBoxControlId = 88124598;
    private const int MissingProviderControlId = 88124597;

    // The host table also declares "Line Key" (id 9) so a link resolved against the HOST would
    // find a field and silently read the wrong row, which is the defect.
    private const string Symbols = """
        {
          "RuntimeVersion": "15.1",
          "Tables": [
            { "Id": 88124520, "Name": "DPV Header", "Fields": [ { "Id": 1, "Name": "No." }, { "Id": 9, "Name": "Line Key" } ] },
            { "Id": 88124521, "Name": "DPV Line", "Fields": [ { "Id": 1, "Name": "Document No." }, { "Id": 3, "Name": "Line Key" } ] },
            { "Id": 88124522, "Name": "DPV Detail", "Fields": [ { "Id": 7, "Name": "Doc Ref" } ] }
          ],
          "Pages": [
            {
              "Id": 88124501,
              "Name": "DPV Host",
              "Properties": [ { "Name": "PageType", "Value": "Card" }, { "Name": "SourceTable", "Value": "88124520" } ],
              "Controls": [
                {
                  "Kind": 6,
                  "RelatedPagePartId": { "Name": "", "Id": 88124502 },
                  "Properties": [ { "Name": "SubPageLink", "Value": "\"Document No.\" = field(\"No.\")" } ],
                  "Id": 88124599,
                  "Name": "Lines"
                },
                {
                  "Kind": 6,
                  "RelatedPagePartId": { "Name": "", "Id": 88124503 },
                  "Properties": [
                    { "Name": "Provider", "Value": "88124599" },
                    { "Name": "SubPageLink", "Value": "\"Doc Ref\" = field(\"Line Key\")" }
                  ],
                  "Id": 88124598,
                  "Name": "FactBox"
                },
                {
                  "Kind": 6,
                  "RelatedPagePartId": { "Name": "", "Id": 88124503 },
                  "Properties": [
                    { "Name": "Provider", "Value": "123" },
                    { "Name": "SubPageLink", "Value": "\"Doc Ref\" = field(\"Line Key\")" }
                  ],
                  "Id": 88124597,
                  "Name": "OrphanProvider"
                }
              ]
            },
            { "Id": 88124502, "Name": "DPV Lines Part", "Properties": [ { "Name": "PageType", "Value": "ListPart" }, { "Name": "SourceTable", "Value": "88124521" } ] },
            { "Id": 88124503, "Name": "DPV FactBox Part", "Properties": [ { "Name": "PageType", "Value": "ListPart" }, { "Name": "SourceTable", "Value": "88124522" } ] }
          ]
        }
        """;

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

    private static XmlElement PartControl(int controlId)
    {
        var dir = TestScratch.Dir("al-runner-dep-part-provider-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, Symbols));
            var xml = RecordPatches.TryBuildDependencyPageMetadata(HostPageId);
            Assert.NotNull(xml);
            var doc = new XmlDocument();
            doc.LoadXml(xml!);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("m", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");
            return (XmlElement)doc.DocumentElement!.SelectSingleNode($"m:Content/m:Containers/m:Controls[@ID='{controlId}']", ns)!;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static XmlElement OnlyLink(XmlElement part)
        => part.ChildNodes.Cast<XmlNode>().OfType<XmlElement>().Single(e => e.LocalName == "SubFormLink");

    [Fact]
    public void ProviderPart_ResolvesFieldLinkAgainstTheProvidersTable_AndCarriesProviderId()
    {
        var part = PartControl(FactBoxControlId);

        Assert.Equal(LinesControlId.ToString(), part.GetAttribute("ProviderID"));
        var link = OnlyLink(part);
        Assert.Equal("FIELD", link.GetAttribute("FilterType"));
        Assert.Equal("7", link.GetAttribute("FieldID"));
        // "Line Key" is field 3 on the PROVIDER's table (DPV Line) and field 9 on the host's; 3 is
        // the provider's, which is the whole claim.
        Assert.Equal("3", link.GetAttribute("FilterValue"));
    }

    [Fact]
    public void PartWithoutProvider_StillResolvesAgainstTheHostAndWritesNoProviderId()
    {
        var part = PartControl(LinesControlId);

        Assert.False(part.HasAttribute("ProviderID"));
        var link = OnlyLink(part);
        Assert.Equal("1", link.GetAttribute("FieldID"));
        Assert.Equal("1", link.GetAttribute("FilterValue"));
    }

    [Fact]
    public void ProviderNamingNoPartOfThePage_IsRefusedNotResolvedAgainstTheHost()
    {
        var part = PartControl(MissingProviderControlId);

        var link = OnlyLink(part);
        Assert.False(int.TryParse(link.GetAttribute("FilterValue"), out _),
            "a Provider that names no part must leave the FIELD name unresolved so SubPageLinks "
            + "refuses by name; resolving it against the host would read the host's 'Line Key' (9)");
    }
}
