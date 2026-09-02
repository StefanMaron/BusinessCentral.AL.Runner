// DependencyPageShapeResolutionTests — pins the runner's own C# predicates behind issue #2341:
// RecordPatches.IsPageShapeKnown and RecordPatches.ResolvePageDeclaresSourceTableForAnyPage
// (AlRunner/Patches/RecordPatches.DependencyPageMetadata.cs).
//
// What this proves, and what it deliberately does NOT
// -----------------------------------------------------
// The BC-observable claim — that a TestPage over a page which happens to ship precompiled
// resolves its SourceTable and positions by primary key — is plain BC behaviour and is
// adjudicated upstream by the al-language corpus against a real service tier (see
// .claude/rules/bc-behavior-tests-go-upstream.md). This file pins the narrower runner-only
// mechanism underneath it, and it is exactly where #2341 lived: the runner already held the
// answer in BcAppSymbolCache and asked a predicate that could not see it.
//
// The negative rows are the load-bearing ones. IsPageShapeKnown answering a blanket true
// would re-open the hole the refusal exists to plug — NavTestPageBase_GetMetaTable would then
// hand back a null NCLMetaTable for a page nobody declared, and PrimaryKeyFields would read
// as empty instead of failing. Likewise ResolvePageDeclaresSourceTableForAnyPage must
// distinguish "the dependency says this page declares none" (false, and BC's own body
// returns null for it) from "no dependency mentions this page at all".

using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Same reason as DependencyPageMetadataXmlTests: BcAppSymbolCache.Get() resolves through the
// process-global CacheRoots override.
[Collection(CacheRootsSerialCollection.Name)]
public class DependencyPageShapeResolutionTests
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

    // Distinctive ids for the same reason DependencyPageMetadataXmlTests picks its own:
    // RecordPatches' dependency-page state is process-global, so an id another test or a real
    // Base Application fixture might also declare could answer from someone else's payload.
    private const int WithSourceTablePageId = 88123421;
    private const int NoSourceTablePageId = 88123422;
    private const int UndeclaredPageId = 88123429;

    // 2000000120 is table "User" — the real SourceTable of Base Application page 9807
    // "User Card", the page issue #2341 was reported against.
    private const string SymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Pages": [
            {
              "Id": 88123421,
              "Name": "DPS Card With Source",
              "Properties": [
                { "Name": "PageType", "Value": "Card" },
                { "Name": "SourceTable", "Value": "2000000120" }
              ]
            },
            {
              "Id": 88123422,
              "Name": "DPS Dialog No Source",
              "Properties": [
                { "Name": "PageType", "Value": "StandardDialog" }
              ]
            }
          ]
        }
        """;

    private static void WithLoadedDependency(Action body)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, SymbolReference));
            body();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // THE REGRESSION ROW. Before #2341 the only "do we know this page?" predicate on the
    // TestPage path was IsPageParsed, which is false for every page the runner did not
    // AL-source-compile — so NavTestPageBase_GetMetaTable refused outright.
    [Fact]
    public void IsPageShapeKnown_PageDeclaredOnlyByADependencyApp_IsKnown()
        => WithLoadedDependency(() =>
        {
            Assert.False(RecordPatches.IsPageParsed(WithSourceTablePageId),
                "the fixture page must NOT be AL-source-parsed, or this proves nothing");
            Assert.True(RecordPatches.IsPageShapeKnown(WithSourceTablePageId));
            Assert.True(RecordPatches.IsPageShapeKnown(NoSourceTablePageId),
                "a dependency page is known even when it declares no SourceTable");
        });

    // The negative direction: a blanket true here would let GetMetaTable answer a null
    // NCLMetaTable for a page nobody declares, which is the silently-empty primary key the
    // refusal exists to prevent.
    [Fact]
    public void IsPageShapeKnown_PageNoLoadedAppDeclares_IsNotKnown()
        => WithLoadedDependency(() =>
            Assert.False(RecordPatches.IsPageShapeKnown(UndeclaredPageId)));

    // The value the whole cluster turned on: 2000000120, read verbatim out of the dependency's
    // own SymbolReference.json. A resolver that answered a default 0 fails here.
    [Fact]
    public void ResolveSourceTableIdForAnyPage_DependencyPage_AnswersTheDeclaredTableId()
        => WithLoadedDependency(() =>
        {
            Assert.Equal(2000000120, RecordPatches.ResolveSourceTableIdForAnyPage(WithSourceTablePageId));
            Assert.True(RecordPatches.ResolvePageDeclaresSourceTableForAnyPage(WithSourceTablePageId));
        });

    // "The dependency says this page declares none" must be distinguishable from "no
    // dependency mentions this page" — the first returns a null NCLMetaTable (BC's own
    // behaviour for SourceTable == 0), the second still throws.
    [Fact]
    public void ResolvePageDeclaresSourceTableForAnyPage_SeparatesDeclaresNoneFromUnknown()
        => WithLoadedDependency(() =>
        {
            Assert.False(RecordPatches.ResolvePageDeclaresSourceTableForAnyPage(NoSourceTablePageId));
            Assert.True(RecordPatches.IsPageShapeKnown(NoSourceTablePageId));

            Assert.False(RecordPatches.ResolvePageDeclaresSourceTableForAnyPage(UndeclaredPageId));
            Assert.False(RecordPatches.IsPageShapeKnown(UndeclaredPageId));
        });

    // The classification the two widened call sites feed: a dependency page with no
    // SourceTable is driven record-less, not demoted to the navigation mock. 167 Base
    // Application pages and 35 System Application pages have this shape on 28.1.
    [Fact]
    public void ConstructionRule_DependencyPageWithoutSourceTable_IsDrivenRecordless()
        => WithLoadedDependency(() =>
            Assert.Equal(
                TestPageClientKind.LiveRecordless,
                TestPageClientConstructionRule.Resolve(
                    recordBuilt: false,
                    pageShapeKnown: RecordPatches.IsPageShapeKnown(NoSourceTablePageId),
                    pageDeclaresSourceTable:
                        RecordPatches.ResolvePageDeclaresSourceTableForAnyPage(NoSourceTablePageId))));

    // …and a page neither source declares still gets the mock, computed the same way.
    [Fact]
    public void ConstructionRule_UndeclaredPage_StaysOnTheNavigationMock()
        => WithLoadedDependency(() =>
            Assert.Equal(
                TestPageClientKind.NavigationMock,
                TestPageClientConstructionRule.Resolve(
                    recordBuilt: false,
                    pageShapeKnown: RecordPatches.IsPageShapeKnown(UndeclaredPageId),
                    pageDeclaresSourceTable:
                        RecordPatches.ResolvePageDeclaresSourceTableForAnyPage(UndeclaredPageId))));
}
