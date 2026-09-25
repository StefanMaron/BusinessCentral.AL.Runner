// DependencyPagePartPropertiesTests — #4282: the properties BC's emitter writes on every subpage
// part (InfopartPageDefinition) of a page reconstructed from SymbolReference.json. The rule and
// its measurements are in docs/dependency-page-properties.md#part-controls.

using System.IO.Compression;
using System.Text;
using System.Xml;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// BcAppSymbolCache.Get() resolves through the process-global CacheRoots override.
[Collection(CacheRootsSerialCollection.Name)]
public class DependencyPagePartPropertiesTests
{
    private const int HostWithAreaId = 88128001;
    private const int HostWithoutAreaId = 88128002;
    private const int PartPageId = 88128003;

    private const int SilentPartId = 88128101;
    private const int StatedPartId = 88128102;
    private const int ExpressionPartId = 88128103;
    private const int OrphanPartId = 88128104;
    private const int QuotedPartId = 88128105;

    // HostWithArea states an ApplicationArea AND a page-level AboutTitle: the first is what an
    // unstated part inherits, the second is a decoy a part must never pick up. The stated part
    // declares a DIFFERENT area than its host, so a reader taking the wrong one is visible.
    internal const string SymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Pages": [
            {
              "Id": 88128001,
              "Name": "PPX Host With Area",
              "Properties": [
                { "Name": "PageType", "Value": "Card" },
                { "Name": "Caption", "Value": "Host; caption" },
                { "Name": "ApplicationArea", "Value": "#Basic,#Suite" },
                { "Name": "AboutTitle", "Value": "Host page title" },
                { "Name": "AboutText", "Value": "Host; page text" }
              ],
              "Controls": [
                {
                  "Kind": 6,
                  "RelatedPagePartId": { "Name": "", "Id": 88128003 },
                  "Id": 88128101,
                  "Name": "SilentPart"
                },
                {
                  "Kind": 6,
                  "RelatedPagePartId": { "Name": "", "Id": 88128003 },
                  "Properties": [
                    { "Name": "ApplicationArea", "Value": "#All" },
                    { "Name": "Editable", "Value": "false" },
                    { "Name": "Enabled", "Value": "false" },
                    { "Name": "Visible", "Value": "false" },
                    { "Name": "ShowFilter", "Value": "0" },
                    { "Name": "AboutTitle", "Value": "Part title" },
                    { "Name": "AboutText", "Value": "Part text" }
                  ],
                  "Id": 88128102,
                  "Name": "StatedPart"
                },
                {
                  "Kind": 6,
                  "RelatedPagePartId": { "Name": "", "Id": 88128003 },
                  "Properties": [
                    { "Name": "Caption", "Value": "Cap = x" },
                    { "Name": "AboutTitle", "Value": "Title; semi" },
                    { "Name": "AboutText", "Value": "Say \"hi\"" }
                  ],
                  "Id": 88128105,
                  "Name": "QuotedPart"
                },
                {
                  "Kind": 1,
                  "Name": "Grp",
                  "Id": 88128190,
                  "Controls": [
                    {
                      "Kind": 6,
                      "RelatedPagePartId": { "Name": "", "Id": 88128003 },
                      "Properties": [
                        { "Name": "Editable", "Value": "IsTenant" },
                        { "Name": "Visible", "Value": "not IsTenant" }
                      ],
                      "Id": 88128103,
                      "Name": "ExpressionPart"
                    }
                  ]
                }
              ]
            },
            {
              "Id": 88128002,
              "Name": "PPX Host Without Area",
              "Properties": [
                { "Name": "PageType", "Value": "Card" },
                { "Name": "Caption", "Value": "\"Quoted\" start" }
              ],
              "Controls": [
                {
                  "Kind": 6,
                  "RelatedPagePartId": { "Name": "", "Id": 88128003 },
                  "Id": 88128104,
                  "Name": "OrphanPart"
                }
              ]
            },
            {
              "Id": 88128003,
              "Name": "PPX Part Page",
              "Properties": [ { "Name": "PageType", "Value": "ListPart" } ]
            }
          ]
        }
        """;

    private static XmlElement Part(int hostId, int partId) => Element(hostId, $"m:Content/m:Containers/m:Controls[@ID='{partId}']");

    private static XmlElement Element(int hostId, string xpath)
    {
        var dir = TestScratch.Dir("al-runner-dep-page-part-props-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".app");
            using (var fs = new FileStream(appPath, FileMode.Create))
            using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
            using (var w = new StreamWriter(za.CreateEntry("SymbolReference.json").Open(), Encoding.UTF8))
                w.Write(SymbolReference);
            RecordPatches.AddBcAppPath(appPath);

            var xml = RecordPatches.TryBuildDependencyPageMetadata(hostId);
            Assert.NotNull(xml);
            var doc = new XmlDocument();
            doc.LoadXml(xml!);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("m", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");
            var part = (XmlElement?)doc.DocumentElement!.SelectSingleNode(xpath, ns);
            Assert.NotNull(part);
            return part!;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string? Attr(XmlElement e, string name) => e.HasAttribute(name) ? e.GetAttribute(name) : null;

    [Fact]
    public void SilentPart_InheritsTheHostApplicationArea()
        => Assert.Equal("#Basic,#Suite", Attr(Part(HostWithAreaId, SilentPartId), "ApplicationArea"));

    [Fact]
    public void StatedPart_KeepsItsOwnApplicationArea_NotTheHosts()
        => Assert.Equal("#All", Attr(Part(HostWithAreaId, StatedPartId), "ApplicationArea"));

    [Fact]
    public void PartOnAHostStatingNoArea_WritesNoApplicationArea()
        => Assert.Null(Attr(Part(HostWithoutAreaId, OrphanPartId), "ApplicationArea"));

    [Fact]
    public void SilentPart_CarriesTheFourDefaultsBcWrites()
    {
        var part = Part(HostWithAreaId, SilentPartId);
        Assert.Equal("true", Attr(part, "Editable"));
        Assert.Equal("true", Attr(part, "Enabled"));
        Assert.Equal("true", Attr(part, "Visible"));
        Assert.Equal("1", Attr(part, "ShowFilter"));
    }

    [Fact]
    public void StatedPart_CarriesItsStatedLiterals()
    {
        var part = Part(HostWithAreaId, StatedPartId);
        Assert.Equal("false", Attr(part, "Editable"));
        Assert.Equal("false", Attr(part, "Enabled"));
        Assert.Equal("false", Attr(part, "Visible"));
        Assert.Equal("0", Attr(part, "ShowFilter"));
    }

    [Fact]
    public void ExpressionPart_PassesItsExpressionsThrough_AndDefaultsTheRest()
    {
        var part = Part(HostWithAreaId, ExpressionPartId);
        Assert.Equal("IsTenant", Attr(part, "Editable"));
        Assert.Equal("not IsTenant", Attr(part, "Visible"));
        Assert.Equal("true", Attr(part, "Enabled"));
        Assert.Equal("1", Attr(part, "ShowFilter"));
        Assert.Equal("#Basic,#Suite", Attr(part, "ApplicationArea"));
    }

    [Fact]
    public void StatedPart_CarriesItsOwnAboutTitleAndText()
    {
        var part = Part(HostWithAreaId, StatedPartId);
        Assert.Equal("ENU=Part title", Attr(part, "AboutTitleML"));
        Assert.Equal("ENU=Part text", Attr(part, "AboutTextML"));
    }

    [Fact]
    public void SilentPart_DoesNotPickUpTheHostPagesAboutTitleOrText()
    {
        var part = Part(HostWithAreaId, SilentPartId);
        Assert.Null(Attr(part, "AboutTitleML"));
        Assert.Null(Attr(part, "AboutTextML"));
    }

    // BC's MultiLanguage parser splits an unquoted value at ';' and reads a leading '"' as the
    // start of a quoted one, so a text carrying ';', '=' or '"' must be written quoted — which
    // is what BC's emitter does (a compiled probe wrote ENU="Title; with semicolon").
    [Fact]
    public void PartTextsCarryingMultiLanguageSyntax_AreQuotedTheWayBcWritesThem()
    {
        var part = Part(HostWithAreaId, QuotedPartId);
        Assert.Equal("ENU=\"Cap = x\"", Attr(part, "CaptionML"));
        Assert.Equal("ENU=\"Title; semi\"", Attr(part, "AboutTitleML"));
        Assert.Equal("ENU=\"Say \"\"hi\"\"\"", Attr(part, "AboutTextML"));
    }

    // The same serializer serves the page's own <Properties> texts: the defect was never
    // specific to parts.
    [Fact]
    public void PageLevelTextCarryingASemicolon_IsQuotedToo()
    {
        var props = Element(HostWithAreaId, "m:Properties");
        Assert.Equal("ENU=\"Host; page text\"", Attr(props, "AboutTextML"));
        Assert.Equal("ENU=Host page title", Attr(props, "AboutTitleML"));
    }

    // The page ROOT CaptionML is the one quoting site BC reads into CaptionMLString, the
    // page-caption path. A ';' anywhere, or a leading '"', changes what BC's own parser returns.
    [Theory]
    [InlineData(HostWithAreaId, "ENU=\"Host; caption\"", "Host; caption")]
    [InlineData(HostWithoutAreaId, "ENU=\"\"\"Quoted\"\" start\"", "\"Quoted\" start")]
    public void RootCaptionML_ParsesBackThroughBcsMultiLanguageToTheFullCaption(int hostId, string written, string caption)
    {
        var root = Element(hostId, ".");
        var captionMl = Attr(root, "CaptionML");
        Assert.Equal(written, captionMl);
        var parsed = Microsoft.Dynamics.Nav.Types.Metadata.MultiLanguage.Parse(captionMl!);
        Assert.Equal(caption, parsed.GetText(1033));
    }
}
