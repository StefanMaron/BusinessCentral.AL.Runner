// DependencyPageSymbolIndexMemoTests — issue #3774, the page-symbol lookup behind
// DependencyObjectSubtype.
//
// What was wrong
// --------------
// TryGetDependencyPageSymbol walked DependencyAppSymbols() — i.e.
// EnumerateRegisteredBcAppSymbols, which File.Exists's every registered .app and then calls
// BcAppSymbolCache.Get on it — and then scanned that app's whole Pages list, ONCE PER CALL.
// DependencyObjectSubtype calls it once per enumerated `page` object, and
// PopulateAllObjWithCaptionVirtualTable runs EnumerateKnownAlObjects on EVERY handout of
// AllObjWithCaption (its `done` guard dedupes ROWS, and sits after the enumerator has already
// yielded — so the subtype lookup is paid even on a pass that inserts nothing).
//
// That is O(objects x apps) file stats where the answer is a dictionary. The stats are not
// avoidable downstream, because BcAppSymbolCache.Get keys ProcessCache on the CONTENT HASH and
// the hash memo keys on statx/(path,length,mtime) — the stat IS the key. Measurements, and why
// the path-keying alternative is refused, are in PR #4223.
//
// What these tests pin, and why a count
// -------------------------------------
// DependencyPageSymbolIndexBuildCountForTests counts genuine walks of the registered-app set.
// The claim is "N lookups at one registration epoch cost ONE walk", which is a count, not a
// duration: a duration assertion would flake on a loaded box and could not tell a memo from a
// fast disk. Correctness is asserted in the same breath — a memo answering a stale or wrong
// PageSymbol would be a far worse defect than the cost it removes, so every test here checks
// the ANSWER as well as the walk count.
//
// Why these are mechanism tests and not corpus tests
// --------------------------------------------------
// Every claim is about how often a runner-internal index is rebuilt inside ONE process, and
// about its lifetime across a registration-set change. Real BC has no
// TryGetDependencyPageSymbol, no _bcAppPaths and no registration epoch; the AL a corpus test
// would run is identical on both sides of this change, because the RESOLVED PageSymbol is
// unchanged — which is exactly what AnswersAreIdentical_AcrossTheMemoBoundary asserts. Same
// reasoning, and the same conclusion, as BcAppRegistrationEpochInvalidationTests (#2888) and
// ObjectIndexMemoTests (#4189), the two neighbours this memo joins.
using System.IO.Compression;
using System.Reflection;
using System.Text;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// RecordPatchesSerialCollection: this class calls RecordPatches.ResetForReload() and registers
// .apps, moving _bcAppPaths and the registration epoch — state ParserStaticsIsolationGuardTests
// requires to be serialized. A concurrent class registering its own .app would move the epoch
// underneath a walk-count assertion and turn it into a flake. Same trade-off, and the same
// reasoning, as BcAppRegistrationEpochInvalidationTests: every .app written here is a fresh
// GUID-named file and BcAppSymbolCache's key is content-addressed, so the worst a concurrent
// CacheRoots override can do is turn a HIT into a MISS and re-parse the same bytes to the same
// result.
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class DependencyPageSymbolIndexMemoTests : IDisposable
{
    private readonly string _root;

    public DependencyPageSymbolIndexMemoTests()
    {
        _root = TestScratch.Dir("al-runner-3774-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // Ids process-wide unique among AlRunner.Tests statics — _bcAppPaths and _parsedPages are
    // process-global and nothing in the test host unregisters an .app. Neighbouring blocks in
    // use: 881234xx/881235xx (DependencyPageMetadataXmlTests), 881236xx
    // (DependencyReportProcessingOnlyTests), 881237xx (TableRelationWhereFieldLinkTests),
    // 881238xx (BcAppRegistrationEpochInvalidationTests), 881239xx
    // (PageRealMetadataEpochRevalidationTests), 881240xx (ObjectIndexMemoTests). This file
    // owns 881241xx.
    private const int FirstPageId = 88124101;
    private const int SecondPageId = 88124102;
    private const int LatePageId = 88124103;
    private const int NeverDeclaredPageId = 88124104;
    private const int SwapPageId = 88124105;
    private const int ContestedPageId = 88124106;

    private static readonly Type T = typeof(RecordPatches);

    private static int BuildCount =>
        (int)T.GetProperty("DependencyPageSymbolIndexBuildCountForTests",
            BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    // Both arguments are passed explicitly. MethodInfo.Invoke does NOT apply a C# optional
    // parameter's default — it throws TargetParameterCountException — so a helper written
    // against the one-argument form silently stops compiling against reality the moment the
    // production signature grows the `surface` parameter. Spelling the surface here also keeps
    // this test honest about which walk it is standing in for.
    private const string Surface = "pages and pageextensions (dependency page metadata)";

    private static BcAppSymbolCache.PageSymbol? Lookup(int pageId) =>
        (BcAppSymbolCache.PageSymbol?)T.GetMethod("TryGetDependencyPageSymbol",
            BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object?[] { pageId, Surface });

    /// <summary>
    /// One .app declaring the given pages, each with a distinct SourceTable so a test can tell
    /// WHICH page's symbol came back rather than merely that one did.
    /// </summary>
    private string WriteApp(params (int Id, string Name, int SourceTableId)[] pages)
    {
        var sb = new StringBuilder();
        sb.Append("{ \"RuntimeVersion\": \"15.1\", \"Namespaces\": [ { \"Name\": \"Issue3774\", \"Pages\": [");
        for (var i = 0; i < pages.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($$"""
                 { "Id": {{pages[i].Id}}, "Name": "{{pages[i].Name}}", "Properties": [
                     { "Name": "PageType", "Value": "Card" },
                     { "Name": "SourceTable", "Value": "{{pages[i].SourceTableId}}" } ] }
                """);
        }
        sb.Append("] } ] }");

        var appPath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".app");
        using var fs = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = za.CreateEntry("SymbolReference.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(sb.ToString());
        return appPath;
    }

    /// <summary>
    /// The cost claim, and the whole point of the issue: N lookups at one registration epoch
    /// must walk the registered-app set at most once.
    ///
    /// <para>Deliberately mixes HITS with a MISS. The miss is the expensive case the old code
    /// had — it scanned every page of every app before answering null, so it could never
    /// short-circuit — and a memo that only served hits would leave DependencyObjectSubtype's
    /// dominant cost in place, because most enumerated objects are not pages at all.</para>
    ///
    /// <para>Asserts on the WALK COUNT, not on elapsed time: a duration assertion cannot
    /// distinguish a memo from a fast disk, and would flake on a loaded CI box.</para>
    /// </summary>
    [Fact]
    public void ManyLookupsAtOneEpoch_WalkTheRegisteredAppsAtMostOnce()
    {
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(WriteApp((FirstPageId, "Issue3774 First", 18)));
        RecordPatches.AddBcAppPath(WriteApp((SecondPageId, "Issue3774 Second", 27)));

        // Prime: the first lookup may legitimately walk, and may be the first walk since
        // another test moved the epoch. The claim is about the lookups AFTER a warm memo.
        Lookup(FirstPageId);
        var before = BuildCount;

        for (var i = 0; i < 25; i++)
        {
            Lookup(FirstPageId);
            Lookup(SecondPageId);
            Lookup(NeverDeclaredPageId);
        }

        var walks = BuildCount - before;
        Assert.True(walks == 0,
            $"75 lookups at one registration epoch walked the registered .apps {walks} time(s); "
            + "expected 0 after priming. An unmemoized TryGetDependencyPageSymbol walks once per "
            + "call — 2 file stats per registered .app per call (#3774).");
    }

    /// <summary>
    /// The correctness claim, and the reason the count claim above is safe to make: the memo
    /// must hand back the SAME PageSymbol the walk did, for pages in different .apps and for a
    /// page nothing declares.
    ///
    /// <para>Asserts a distinct SourceTableId per page rather than merely non-null: a memo
    /// keyed or merged wrongly would hand out the OTHER page's symbol, which a non-null check
    /// cannot see and which is a worse bug than the cost it removed. The two ids are asserted
    /// to differ first, so a fixture that accidentally gave both pages the same SourceTable
    /// could not make this pass vacuously.</para>
    /// </summary>
    [Fact]
    public void AnswersAreIdentical_AcrossTheMemoBoundary()
    {
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(WriteApp((FirstPageId, "Issue3774 First", 18)));
        RecordPatches.AddBcAppPath(WriteApp((SecondPageId, "Issue3774 Second", 27)));

        // Distinct by construction — pin it, so the discrimination below is real.
        Assert.NotEqual(18, 27);

        for (var i = 0; i < 3; i++)
        {
            var first = Lookup(FirstPageId);
            Assert.NotNull(first);
            Assert.Equal(FirstPageId, first!.Id);
            Assert.Equal(18, first.SourceTableId);

            var second = Lookup(SecondPageId);
            Assert.NotNull(second);
            Assert.Equal(SecondPageId, second!.Id);
            Assert.Equal(27, second.SourceTableId);

            // A page no registered .app declares stays a truthful null — never a stale hit.
            Assert.Null(Lookup(NeverDeclaredPageId));
        }
    }

    /// <summary>
    /// The GROW-direction invalidation claim: a page declared by an .app registered AFTER the
    /// memo was built must be found.
    ///
    /// <para>This is the direction that costs something. Every caller reads this lookup as
    /// <c>TryGetDependencyPageSymbol(id)?.X ?? default</c>, so a memo that never invalidated
    /// would answer <c>SourceTableId = 0</c>, <c>PageType = null</c>,
    /// <c>IsPageShapeKnown = false</c> for a page the runner does now know about — the exact
    /// "not a missing answer but a wrong one" #3143 rewrote this walk to prevent.</para>
    ///
    /// <para>The absence is asserted BEFORE the late registration rather than assumed: without
    /// that, a page left registered by an earlier test in the same process would make this pass
    /// without invalidating anything.</para>
    /// </summary>
    [Fact]
    public void APageDeclaredByAnAppRegisteredAfterTheMemoWasBuilt_IsStillFound()
    {
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(WriteApp((FirstPageId, "Issue3774 First", 18)));

        // Build the memo at an epoch that does NOT know about the late page.
        Assert.Equal(18, Lookup(FirstPageId)!.SourceTableId);
        Assert.Null(Lookup(LatePageId));

        RecordPatches.AddBcAppPath(WriteApp((LatePageId, "Issue3774 Late", 36)));

        var late = Lookup(LatePageId);
        Assert.NotNull(late);
        Assert.Equal(36, late!.SourceTableId);
        // The earlier entry survives the rebuild — invalidation must rebuild, never truncate.
        Assert.Equal(18, Lookup(FirstPageId)!.SourceTableId);
    }

    /// <summary>
    /// The ORDERING claim: when two registered .apps both declare the same page id, the
    /// FIRST-registered one wins.
    ///
    /// <para>Reachable, not hypothetical: <c>AddBcAppPath</c> dedupes on the PATH only
    /// (<c>_bcAppPaths.Contains(appPath, OrdinalIgnoreCase)</c>,
    /// RecordPatches.BcAppFallback.cs:451), so two different .app FILES may each declare page N
    /// and which one answers is a real observable.</para>
    ///
    /// <para>This pins what makes the memo a pure memoization rather than a behaviour change.
    /// The walk it replaced returned on its first match over
    /// <see cref="EnumerateRegisteredBcAppSymbols"/>, which yields in registration order; the
    /// index reproduces that with <c>TryAdd</c>, whose first write wins. Nothing else in this
    /// suite can see the property — every other test gives each .app its own page ids, so
    /// reversing the index walk leaves all of them GREEN (measured in review of PR #4223:
    /// <c>Failed: 0, Passed: 570</c> with LAST-registered winning).</para>
    ///
    /// <para>The two SourceTableIds differ by construction and the assertion names the
    /// expected one concretely, so a reversal answers <c>Expected: 111 / Actual: 222</c>
    /// rather than merely "not null".</para>
    /// </summary>
    [Fact]
    public void WhenTwoAppsDeclareTheSamePageId_TheFirstRegisteredWins()
    {
        RecordPatches.ResetForReload();

        // Two DISTINCT .app files — WriteApp gives each a fresh GUID name, so the path-keyed
        // dedupe at AddBcAppPath registers both rather than collapsing them.
        var firstRegistered = WriteApp((ContestedPageId, "Issue3774 Contested First", 111));
        var secondRegistered = WriteApp((ContestedPageId, "Issue3774 Contested Second", 222));
        Assert.NotEqual(firstRegistered, secondRegistered);

        RecordPatches.AddBcAppPath(firstRegistered);
        RecordPatches.AddBcAppPath(secondRegistered);

        var resolved = Lookup(ContestedPageId);
        Assert.NotNull(resolved);
        Assert.Equal(ContestedPageId, resolved!.Id);
        Assert.Equal(111, resolved.SourceTableId);

        // Repeat across the memo boundary: the served answer must carry the same precedence as
        // the build did, not merely happen to be right on the pass that built the index.
        Assert.Equal(111, Lookup(ContestedPageId)!.SourceTableId);
    }

    /// <summary>
    /// The refusal claim: an .app that becomes unreadable after registration must still raise
    /// <see cref="BcAppSymbolReadException"/> naming the surface the CALLER was reading, even
    /// though the walk now happens inside the shared index build.
    ///
    /// <para>This is a regression the memo actually caused and this suite caught (PR #4223):
    /// building the index through <c>DependencyAppSymbols()</c> made every caller's refusal say
    /// "pages and pageextensions (dependency page metadata)", so AllObj's walk stopped naming
    /// itself. <c>loud-failures.md</c> requires the message to name what was being read, and
    /// #3143 rewrote these walks precisely so a swallowed read could not masquerade as "this
    /// .app declares nothing" — a memo that blurred the surface would have eroded half of
    /// that.</para>
    ///
    /// <para>Also pins that the refusal is REPEATABLE: a build that throws must not publish a
    /// partial index, or the second call would serve a short answer instead of refusing. That
    /// is asserted by throwing twice, and it is the property that makes the memo safe to share
    /// between walks at all.</para>
    /// </summary>
    [Fact]
    public void AnUnreadableAppRefusesNamingTheCallersSurface_AndKeepsRefusing()
    {
        RecordPatches.ResetForReload();
        var appPath = WriteApp((FirstPageId, "Issue3774 First", 18));
        RecordPatches.AddBcAppPath(appPath);

        // Readable at registration, then corrupted underneath the runner — the shape #3143
        // exists for. Rewriting the bytes also moves the content hash, so BcAppSymbolCache
        // cannot serve the earlier parse from ProcessCache.
        File.WriteAllText(appPath, "not a zip archive at all");

        const string callerSurface = "objects (AllObj)";
        var lookup = T.GetMethod("TryGetDependencyPageSymbol",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var first = Assert.Throws<TargetInvocationException>(
            () => lookup.Invoke(null, new object?[] { FirstPageId, callerSurface }));
        var inner = Assert.IsType<BcAppSymbolReadException>(first.InnerException);
        Assert.Contains(callerSurface, inner.Message);

        // Again — a partial index must not have been published by the throwing build.
        var second = Assert.Throws<TargetInvocationException>(
            () => lookup.Invoke(null, new object?[] { FirstPageId, callerSurface }));
        Assert.IsType<BcAppSymbolReadException>(second.InnerException);
    }

    /// <summary>
    /// The ABA claim (#2888): a same-COUNT swap of the registered set must be observed.
    ///
    /// <para>Both reads happen at the same registered-app count — one .app is registered, the
    /// set is cleared, and a DIFFERENT .app declaring the SAME page id with a DIFFERENT
    /// SourceTable is registered. A memo keyed on <c>_bcAppPaths.Count</c> reads the two
    /// epochs as one generation and replays the first .app's answer; only the monotonic
    /// registration epoch distinguishes them. <c>NoGenerationKeyUsesTheAppPathCount</c> pins
    /// this structurally across all of AlRunner/Patches; this test pins it behaviourally for
    /// the index added here, because a structural grep cannot tell whether the term it found
    /// is load-bearing.</para>
    ///
    /// <para>The answer flips 18 → 42, a concrete value in each epoch, so a green cannot mean
    /// "the second .app was unreadable and null was an honest answer".</para>
    /// </summary>
    [Fact]
    public void ASameCountSwapOfTheRegisteredApps_IsNotServedFromTheMemo()
    {
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(WriteApp((SwapPageId, "Issue3774 Swap", 18)));
        Assert.Equal(18, Lookup(SwapPageId)!.SourceTableId);

        // Same count on both sides: exactly one registered .app before and after.
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(WriteApp((SwapPageId, "Issue3774 Swap", 42)));

        var after = Lookup(SwapPageId);
        Assert.NotNull(after);
        Assert.Equal(42, after!.SourceTableId);
    }
}
