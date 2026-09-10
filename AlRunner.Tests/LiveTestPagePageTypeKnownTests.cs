// LiveTestPagePageTypeKnownTests — issue #3735.
//
// THE QUESTION
//   BuiltInPageModeActionRule.ResolveShape answers RefuseUnknownPageType when
//   RecordPatches.TryGetAnyPageType(pageId) is null, and LiveNavTestPage.BuiltInPageModeActionFor
//   turns that into a RunnerOutOfScopeException. #3735 asked for a second source for that
//   PageType — and, first, for a reproducer, because no test could produce a page the runner
//   drives live while knowing nothing about its type.
//
// THE ANSWER: there is no such page, and this file pins why.
//   TryGetAnyPageType is null for exactly the ids IsPageShapeKnown answers false for — both
//   read _parsedPages and then a loaded dependency .app's page symbols, and NEITHER source can
//   yield a null PageType, because both apply AL's absent-property default of "Card"
//   (RecordPatches.AlPageParser.cs's ParsedPage construction; BcAppSymbolCache.TryParsePageSymbol's
//   `IsNullOrWhiteSpace(pageType) ? "Card"`). And both routes to a live client require one of
//   those two inventories: LiveOverRecord needs a source table, which TestPageFactory.TryBuild
//   resolves through the same two lookups, and LiveRecordless needs pageShapeKnown outright.
//   A page in neither gets MockITestPage, whose View()/Edit() are the base mock's and never
//   reach BuiltInPageModeActionFor at all.
//
//   So the refusal STAYS as a guard on a state no AL can reach today, and the diagnosis is what
//   #3735 wanted: BC's own captured emitter metadata (docs/object-metadata-capture.md) is not
//   the second source, because it exists only for objects THIS run compiles — a subset of
//   _parsedPages. A precompiled dependency page never passes through Compilation.Emit. The
//   source that would widen the inventory is a runtime-package reader (#3537).
//
// WHAT WOULD MAKE THE REFUSAL REACHABLE, which is what these rows guard
//   Either half of the co-extensiveness breaking: a third inventory feeding IsPageShapeKnown or
//   TestPageFactory.TryBuild without also feeding TryGetAnyPageType, or either existing source
//   passing an absent PageType property through as null instead of defaulting it.
//
// No Base Application floor (.claude/rules/no-base-app-in-csharp-tests.md).

using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Same reason as DependencyPageShapeResolutionTests: BcAppSymbolCache.Get() resolves through
// the process-global CacheRoots override.
[Collection(CacheRootsSerialCollection.Name)]
public class LiveTestPagePageTypeKnownTests
{
    // Ids distinct from every other fixture's: RecordPatches' dependency-page state is
    // process-global, so a shared id could answer from someone else's payload.
    private const int TypedPageId = 88373501;       // PageType = List, SourceTable declared
    private const int NoPageTypePropId = 88373502;  // no PageType property at all
    private const int BlankPageTypeId = 88373503;   // PageType present but empty
    private const int NoSourceNoTypeId = 88373504;  // neither property — the recordless shape
    private const int UndeclaredPageId = 88373509;  // no loaded .app declares it

    // 2000000120 is table "User", the same real table DependencyPageShapeResolutionTests uses,
    // so the SourceTable rows resolve against a table that genuinely exists.
    private const string SymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Pages": [
            {
              "Id": 88373501,
              "Name": "LTP Typed List",
              "Properties": [
                { "Name": "PageType", "Value": "List" },
                { "Name": "SourceTable", "Value": "2000000120" }
              ]
            },
            {
              "Id": 88373502,
              "Name": "LTP No PageType Property",
              "Properties": [
                { "Name": "SourceTable", "Value": "2000000120" }
              ]
            },
            {
              "Id": 88373503,
              "Name": "LTP Blank PageType",
              "Properties": [
                { "Name": "PageType", "Value": "" },
                { "Name": "SourceTable", "Value": "2000000120" }
              ]
            },
            {
              "Id": 88373504,
              "Name": "LTP No Source No Type",
              "Properties": []
            }
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

    private static void WithLoadedDependency(Action body)
    {
        var dir = TestScratch.Dir("al-runner-3735-tests");
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

    /// <summary>
    /// Every page id a dependency declares answers a CONCRETE PageType, whether or not the
    /// property is there — the arm that fails if either source ever passes an absent property
    /// through as null. "Card" is AL's own default, not a runner invention.
    /// </summary>
    [Theory]
    [InlineData(TypedPageId, "List")]
    [InlineData(NoPageTypePropId, "Card")]
    [InlineData(BlankPageTypeId, "Card")]
    [InlineData(NoSourceNoTypeId, "Card")]
    public void DependencyPage_AnswersAConcretePageType_EvenWithThePropertyAbsent(
        int pageId, string expected)
        => WithLoadedDependency(() =>
        {
            Assert.False(RecordPatches.IsPageParsed(pageId),
                "the fixture pages must NOT be AL-source-parsed, or this proves nothing");
            Assert.Equal(expected, RecordPatches.TryGetAnyPageType(pageId));
        });

    /// <summary>
    /// The co-extensiveness itself: null PageType and unknown shape are the same set. A third
    /// inventory wired into one predicate and not the other breaks this row before it can make
    /// the refusal reachable.
    /// </summary>
    [Theory]
    [InlineData(TypedPageId, true)]
    [InlineData(NoPageTypePropId, true)]
    [InlineData(BlankPageTypeId, true)]
    [InlineData(NoSourceNoTypeId, true)]
    [InlineData(UndeclaredPageId, false)]
    public void PageTypeIsNonNull_ExactlyWhenThePageShapeIsKnown(int pageId, bool known)
        => WithLoadedDependency(() =>
        {
            Assert.Equal(known, RecordPatches.IsPageShapeKnown(pageId));
            Assert.Equal(known, RecordPatches.TryGetAnyPageType(pageId) != null);
        });

    /// <summary>
    /// The LiveOverRecord precondition. TestPageFactory.TryBuild builds a record only when a
    /// source table resolves, and it resolves one through the SAME two lookups TryGetAnyPageType
    /// reads — so a resolvable source table implies a known PageType, and an unknown page
    /// resolves none. Concrete table id, not "non-zero": a resolver answering a default fails.
    /// </summary>
    [Fact]
    public void AResolvableSourceTable_ImpliesAKnownPageType()
        => WithLoadedDependency(() =>
        {
            Assert.Equal(2000000120, RecordPatches.ResolveSourceTableIdForAnyPage(TypedPageId));
            Assert.NotNull(RecordPatches.TryGetAnyPageType(TypedPageId));

            Assert.Equal(0, RecordPatches.ResolveSourceTableIdForAnyPage(UndeclaredPageId));
            Assert.Null(RecordPatches.TryGetAnyPageType(UndeclaredPageId));
        });

    /// <summary>
    /// The LiveRecordless precondition: it is <c>pageShapeKnown</c> itself, so a page whose
    /// PageType is unknown cannot take that route either — it lands on the navigation mock.
    /// Both live kinds are asserted here so a rule change that widened the mock case into a
    /// live one would fail rather than silently make the refusal reachable.
    /// </summary>
    [Fact]
    public void WithoutARecord_OnlyAKnownPageIsDrivenLive()
        => WithLoadedDependency(() =>
        {
            Assert.Equal(TestPageClientKind.LiveRecordless, ClientKindFor(NoSourceNoTypeId));
            Assert.Equal(TestPageClientKind.NavigationMock, ClientKindFor(UndeclaredPageId));
        });

    // Computed exactly the way CodeunitPatches.CreateTestPageClient computes it, with
    // recordBuilt false — the branch a page with no resolvable source table takes.
    private static TestPageClientKind ClientKindFor(int pageId)
        => TestPageClientConstructionRule.Resolve(
            recordBuilt: false,
            pageShapeKnown: RecordPatches.IsPageShapeKnown(pageId),
            pageDeclaresSourceTable: RecordPatches.ResolvePageDeclaresSourceTableForAnyPage(pageId));

    /// <summary>
    /// The last link: the client an unknown page actually gets answers View()/Edit() out of the
    /// base mock, so <c>LiveNavTestPage.BuiltInPageModeActionFor</c> — and therefore #3735's
    /// refusal — is never entered for it. The user-visible signal for such a page is the
    /// <c>[warn] … navigation mock</c> line CodeunitPatches.CreateTestPageClient prints.
    /// </summary>
    [Fact]
    public void TheClientAnUnknownPageGets_AnswersViewAndEditFromTheMock()
    {
        var mock = new MockITestPage();
        Assert.IsType<MockITestAction>(mock.View());
        Assert.IsType<MockITestAction>(mock.Edit());
    }
}
