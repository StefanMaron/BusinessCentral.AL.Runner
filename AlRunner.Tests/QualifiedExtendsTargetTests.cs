// QualifiedExtendsTargetTests — #5085: a source pageextension (and tableextension) whose `extends`
// clause names its base object with a namespace qualifier must attach to that object.
//
// The parse used to keep the clause as raw text, so `extends QxA."QX Card"` reached the matcher
// as `QxA."QX Card` and equalled no page name: the extension attached to nothing and its triggers
// never ran. These drive the real parsers by reflection, exactly like ActionRefTargetParserTests,
// then read the registries the runtime dispatches from.
//
// The same-name cases use two pages that share a name in different namespaces. One app cannot
// declare that (the compiler reports AL0197), so the shapes are a second source app, or a source
// page named like a page a dependency .app ships; the registry keys by id and holds both. The
// end-to-end claim against a real service tier is the corpus's
// (pageextensiontrigger/TestPageExtQualifiedExtends_Tests.al).
using System.IO.Compression;
using System.Reflection;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// RecordPatchesSerialCollection: registers a .app and calls ResetForReload, moving the registration
// epoch other classes in that collection assert on.
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class QualifiedExtendsTargetTests : IDisposable
{
    private static readonly Type RP = typeof(RecordPatches);

    // Process-wide unique among AlRunner.Tests statics: this file owns 881270xx.
    private const int PageA = 88127001;       // "QX Card" in namespace QxA
    private const int PageB = 88127002;       // "QX Card" in namespace QxB
    private const int PageDep = 88127003;     // "QX Card" in a dependency .app, namespace QxDep
    private const int PagePlain = 88127004;   // "QX Plain" in namespace QxA
    private const int ExtQualA = 88127011;    // extends QxA."QX Card"
    private const int ExtUnqualA = 88127012;  // extends "QX Card" with `using QxA`
    private const int ExtQualB = 88127013;    // extends QxB."QX Card"
    private const int ExtQualDep = 88127014;  // extends QxDep."QX Card"
    private const int ExtPlain = 88127015;    // extends "QX Plain" with `using QxA`
    private const int ExtOwnNamespace = 88127016;  // namespace QxA, extends "QX Card"
    private const int ExtElsewhere = 88127017;     // namespace QxElsewhere, extends "QX Card"
    private const int TableA = 88127021;
    private const int TableExtQual = 88127031;
    private const int TableExtUnqual = 88127032;

    private readonly string _root = TestScratch.Dir("al-runner-5085-tests");

    public QualifiedExtendsTargetTests()
    {
        Directory.CreateDirectory(_root);
        RecordPatches.ResetForReload();
    }

    public void Dispose()
    {
        RecordPatches.ResetForReload();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static void Parse(string method, string source) =>
        RP.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.InvokeStatic(source);

    private static string PageIn(string ns, int id, string name) => $$"""
        namespace {{ns}};

        page {{id}} "{{name}}"
        {
            PageType = Card;
            layout { area(content) { } }
        }
        """;

    private static string ExtensionIn(string? ns, string? usingNs, int id, string extendsClause) =>
        (ns is null ? "" : $"namespace {ns};\n") + (usingNs is null ? "" : $"using {usingNs};\n") + $$"""

        pageextension {{id}} "QX Ext {{id}}" extends {{extendsClause}}
        {
            trigger OnOpenPage() begin end;
        }
        """;

    private string WriteDependencyApp()
    {
        var json = $$"""
            { "RuntimeVersion": "15.1", "Namespaces": [ { "Name": "QxDep",
              "Pages": [ { "Id": {{PageDep}}, "Name": "QX Card", "Properties": [ { "Name": "PageType", "Value": "Card" } ] } ] } ] }
            """;
        var appPath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".app");
        using var fs = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(fs, ZipArchiveMode.Create);
        using var w = new StreamWriter(za.CreateEntry("SymbolReference.json").Open(), Encoding.UTF8);
        w.Write(json);
        return appPath;
    }

    private static void ParseSourcePagesAndExtensions()
    {
        Parse("TryParsePageFile", PageIn("QxA", PageA, "QX Card"));
        Parse("TryParsePageFile", PageIn("QxB", PageB, "QX Card"));
        Parse("TryParsePageFile", PageIn("QxA", PagePlain, "QX Plain"));
        Parse("TryParsePageFile", ExtensionIn(null, null, ExtQualA, "QxA.\"QX Card\""));
        Parse("TryParsePageFile", ExtensionIn("QxImporter", "QxA", ExtUnqualA, "\"QX Card\""));
        Parse("TryParsePageFile", ExtensionIn(null, null, ExtQualB, "QxB.\"QX Card\""));
        Parse("TryParsePageFile", ExtensionIn("QxImporter", "QxA", ExtPlain, "\"QX Plain\""));
    }

    // Positive: the reported shape. A qualified clause attaches the extension to the page it names.
    [Fact]
    public void QualifiedExtends_AttachesToTheNamedSourcePage()
    {
        ParseSourcePagesAndExtensions();

        Assert.Contains(ExtQualA, RecordPatches.GetPageExtensionIdsForPage(PageA));
    }

    // Positive control: the unqualified spelling, which always worked, still attaches.
    [Fact]
    public void UnqualifiedExtends_StillAttachesToItsPage()
    {
        ParseSourcePagesAndExtensions();

        Assert.Equal(new[] { ExtPlain }, RecordPatches.GetPageExtensionIdsForPage(PagePlain));
        Assert.Contains(ExtUnqualA, RecordPatches.GetPageExtensionIdsForPage(PageA));
    }

    // Negative: two pages share a name; the qualifier decides which one the extension is for.
    // Matching by short name alone would put ExtQualA on page B and ExtQualB on page A.
    [Fact]
    public void QualifiedExtends_DoesNotAttachToASameNamedPageInAnotherNamespace()
    {
        ParseSourcePagesAndExtensions();

        var onA = RecordPatches.GetPageExtensionIdsForPage(PageA);
        var onB = RecordPatches.GetPageExtensionIdsForPage(PageB);

        Assert.Contains(ExtQualA, onA);
        Assert.DoesNotContain(ExtQualB, onA);
        Assert.Contains(ExtQualB, onB);
        Assert.DoesNotContain(ExtQualA, onB);
        // An extension that never names the namespace is not a namespace match, and the page of
        // the unrelated name is untouched by any of them.
        Assert.DoesNotContain(ExtQualA, RecordPatches.GetPageExtensionIdsForPage(PagePlain));
        Assert.DoesNotContain(ExtQualB, RecordPatches.GetPageExtensionIdsForPage(PagePlain));
    }

    // Negative, the cross-app shape: a source page and a dependency's page share a name. A
    // qualifier naming the dependency's namespace is the dependency's page, and one naming the
    // source page's namespace is the source page.
    [Fact]
    public void QualifiedExtends_SeparatesASourcePageFromADependencyPageOfTheSameName()
    {
        RecordPatches.AddBcAppPath(WriteDependencyApp());
        ParseSourcePagesAndExtensions();
        Parse("TryParsePageFile", ExtensionIn(null, null, ExtQualDep, "QxDep.\"QX Card\""));

        var onDependency = RecordPatches.GetPageExtensionIdsForPage(PageDep);
        var onSource = RecordPatches.GetPageExtensionIdsForPage(PageA);

        Assert.Contains(ExtQualDep, onDependency);
        Assert.DoesNotContain(ExtQualA, onDependency);
        Assert.DoesNotContain(ExtQualB, onDependency);
        Assert.Contains(ExtQualA, onSource);
        Assert.DoesNotContain(ExtQualDep, onSource);
    }

    // An unqualified clause from inside the namespace that declares a same-named page is that page:
    // the compiler looks in the file's own namespace first, so it is not the dependency's.
    [Fact]
    public void UnqualifiedExtends_PrefersTheSourcePageOfItsOwnNamespaceOverADependencyPage()
    {
        RecordPatches.AddBcAppPath(WriteDependencyApp());
        ParseSourcePagesAndExtensions();
        Parse("TryParsePageFile", ExtensionIn("QxA", null, ExtOwnNamespace, "\"QX Card\""));
        // Control: the same unqualified clause from a namespace declaring no such page, and
        // importing none, is left to the dependency's page.
        Parse("TryParsePageFile", ExtensionIn("QxElsewhere", null, ExtElsewhere, "\"QX Card\""));

        Assert.Contains(ExtOwnNamespace, RecordPatches.GetPageExtensionIdsForPage(PageA));
        Assert.DoesNotContain(ExtOwnNamespace, RecordPatches.GetPageExtensionIdsForPage(PageDep));
        Assert.Contains(ExtElsewhere, RecordPatches.GetPageExtensionIdsForPage(PageDep));
    }

    // The registry affectedOnly selects from (#5025) answers the same question per extension.
    [Fact]
    public void PageExtensionBasePageIds_ResolvesAQualifiedClauseToTheNamedPage()
    {
        RecordPatches.AddBcAppPath(WriteDependencyApp());
        ParseSourcePagesAndExtensions();
        Parse("TryParsePageFile", ExtensionIn(null, null, ExtQualDep, "QxDep.\"QX Card\""));

        var bases = RecordPatches.PageExtensionBasePageIds();

        Assert.NotNull(bases);
        Assert.Equal(new[] { PageA }, bases![ExtQualA]);
        Assert.Equal(new[] { PageB }, bases[ExtQualB]);
        Assert.Equal(new[] { PageDep }, bases[ExtQualDep]);
        Assert.Equal(new[] { PagePlain }, bases[ExtPlain]);
    }

    // The table-side twin: a tableextension's qualified clause reached the extension registry as
    // `QxA."QX Tab` and registered against no table.
    [Fact]
    public void QualifiedTableExtends_RegistersAgainstTheNamedTable()
    {
        Parse("TryParseTableFile", $$"""
            namespace QxA;

            table {{TableA}} "QX Tab"
            {
                fields { field(1; Code; Code[10]) { } }
                keys { key(PK; Code) { Clustered = true; } }
            }
            """);
        Parse("TryParseTableExtensionFile", $$"""
            using QxA;

            tableextension {{TableExtQual}} "QX Tab Qualified" extends QxA."QX Tab"
            {
                fields { field(88127101; "QX Added"; Integer) { } }
            }
            """);
        Parse("TryParseTableExtensionFile", $$"""
            using QxA;

            tableextension {{TableExtUnqual}} "QX Tab Unqualified" extends "QX Tab"
            {
                fields { field(88127102; "QX Added Two"; Integer) { } }
            }
            """);

        Assert.Equal(new[] { TableA }, RecordPatches.ExtensionBaseObjectIds("TableExtension", TableExtQual));
        Assert.Equal(new[] { TableA }, RecordPatches.ExtensionBaseObjectIds("TableExtension", TableExtUnqual));
    }
}
