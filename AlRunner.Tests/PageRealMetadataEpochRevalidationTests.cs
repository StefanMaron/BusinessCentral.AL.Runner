// PageRealMetadataEpochRevalidationTests — issue #3011, the PAGE-side instance of the
// "state derived from _bcAppPaths outlives a change to _bcAppPaths" family that #2944
// (_depPageMetadataXml / _depReportMetadataXml) and #2888 (_bcMissCache, the seven
// count-keyed generations, NavReportSync._realMetaCache) worked through on the table and
// report sides.
//
// The state, and why it is the same defect
// ----------------------------------------
// RecordPatches.EnsureRealPageMetadata gives exactly two NEGATIVE answers, and both are
// derived from the registered .app set:
//
//   * "no NCLMetaForm could be built for this id" — BuildNCLMetaForm's own existence check
//     calls HasDependencyPageMetadata, which walks _bcAppPaths; and _metaFormCache memoizes
//     the resulting null;
//   * "LoadMetadata() threw" — the load reaches TryBuildDependencyPageMetadata, the memo
//     #2889/#2944 fixed, so a null taken there before a registration produced a throw the
//     old _pagesRealMetadataFailed set then remembered forever.
//
// The SHRINK direction was covered (ResetForReload -> ResetPageMetadataForReload, #1957).
// The GROW direction was not: nothing revisited either answer when AddBcAppPath registered
// the .app that would now supply the metadata, so the page answered null for the rest of
// the process — a second-order memo masking the first-order fix for its own consumer,
// which is precisely NavReportSync._realMetaCache's shape one subsystem over.
//
// What changed
// ------------
// Both negative answers now share ONE record, stamped with the .app registration epoch
// (#2888's monotonic counter) they were taken at, and are retaken exactly once when that
// epoch moves. The success set is deliberately not stamped — see the field comment in
// RecordPatches.RealPageMetadata.cs, and #1957 for what re-running a load on an already
// loaded instance costs.
//
// Why these are mechanism tests and not corpus tests
// --------------------------------------------------
// Every claim here is about cache lifetime across a registration-set change inside ONE
// runner process, which .claude/rules/bc-behavior-tests-go-upstream.md names as explicitly
// runner-specific: real BC has no _bcAppPaths, no _metaFormCache and no on-demand
// "opt this page into a real metadata load" memo at all — on a service tier a page's
// metadata is simply there. There is no AL a corpus test could run that would distinguish
// the two builds, and by the reachability analysis in #3011 the miss-then-register ordering
// is not even reachable from AL today: every EnsureRealPageMetadata caller is on the
// test-execution path, which runs after registration completes. The defect is latent, and a
// latent defect's proving test is a state-lifetime property, not a scenario.
//
// What the assertions are, and why a count
// ----------------------------------------
// The observable is the DECISION — was the question re-asked, and exactly once per epoch —
// not the NCLMetaForm that comes back. That is deliberate and not a convenience: whether a
// real NCLMetaForm can be built at all depends on the BC engine being loaded in-process,
// which this test host does not guarantee (BcEngineBootstrap can and does report
// SkipReason on a box with no usable artifacts). Asserting on the returned object would
// make the test's meaning depend on the environment; asserting on the retake count and the
// stamp does not, and both are still impossible to satisfy with a no-op — a build that
// never retakes fails the positive arm, and one that always retakes fails the bound.
using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// RecordPatchesSerialCollection: this class calls RecordPatches.ResetForReload() directly,
// which ParserStaticsIsolationGuardTests requires. Same trade-off against
// CacheRootsSerialCollection as BcAppRegistrationEpochInvalidationTests, and the same
// reasoning: every .app written here is a fresh GUID-named file and BcAppSymbolCache's key
// is content-addressed, so a concurrent CacheRoots override can at worst turn a HIT into a
// MISS and re-parse the same bytes to the same answer.
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class PageRealMetadataEpochRevalidationTests : IDisposable
{
    private readonly string _root;

    public PageRealMetadataEpochRevalidationTests()
    {
        _root = TestScratch.Dir("al-runner-3011-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // Process-wide unique ids: _bcAppPaths, _metaFormCache, AlPageMetadataRegistry and the
    // negative record are all process-global and nothing in the test host unregisters an
    // .app. Neighbouring blocks in use: 881234xx (DependencyPageMetadataXmlTests), 881235xx
    // (DependencyMetadataMemoInvalidationTests), 881236xx (DependencyReportProcessingOnlyTests),
    // 881237xx (TableRelationWhereFieldLinkTests), 881238xx
    // (BcAppRegistrationEpochInvalidationTests). This file owns 881239xx.
    private const int LateDeclaredPageId = 88123901;
    private const int BoundedRetakePageId = 88123902;
    private const int SteadyStatePageId = 88123903;
    private const int NoMetadataSourcePageId = 88123904;
    private const int UnrelatedPageId = 88123905;

    private string WriteApp(string symbolReferenceJson)
    {
        var appPath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".app");
        using var zip = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(zip, ZipArchiveMode.Create);
        var entry = za.CreateEntry("SymbolReference.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(symbolReferenceJson);
        return appPath;
    }

    /// <summary>Top-level "Pages" container, the same shape DependencyPageMetadataXmlTests
    /// writes and BcAppSymbolCache.PageSymbol parses.</summary>
    private static string PagesJson(int pageId, string name) => $$"""
        {
          "RuntimeVersion": "15.1",
          "Pages": [
            {
              "Id": {{pageId}},
              "Name": "{{name}}",
              "Properties": [
                { "Name": "Caption", "Value": "{{name}}" },
                { "Name": "PageType", "Value": "Card" }
              ]
            }
          ]
        }
        """;

    /// <summary>
    /// The runner's own emit-captured metadata XML is what opens
    /// <see cref="RecordPatches.EnsureRealPageMetadata"/>'s gate; without it the method
    /// returns before it ever consults the negative record, so nothing under test is
    /// reached. Registering it is exactly what BcCompiler.Emit does for a page the runner
    /// compiled itself — the content is irrelevant here, only the gate is.
    /// </summary>
    private static void OpenTheGateFor(int pageId) =>
        AlPageMetadataRegistry.Register(pageId, $"<PageDefinition ID=\"{pageId}\" />");

    // ── the defect: the GROW direction ───────────────────────────────────────

    /// <summary>
    /// A negative answer taken before the .app declaring the page was registered must not
    /// survive that registration.
    ///
    /// <para>Both halves are asserted with CONCRETE epoch values rather than "not null":
    /// the stamp must be the epoch current when the answer was taken, must still be that
    /// epoch while nothing has registered, and must NOT still be that epoch once something
    /// has. A build that recorded the answer and never revisited it — main's behaviour
    /// before #3011 — fails the last assertion; one that recorded no stamp at all fails the
    /// first.</para>
    ///
    /// <para>After the retake the record may be gone entirely rather than re-stamped: with
    /// a usable BC engine in-process the rebuilt metaform can load and the answer becomes
    /// positive. Both outcomes are "the question was retaken", which is the claim; what is
    /// excluded either way is the answer still standing at the OLD epoch.</para>
    /// </summary>
    [Fact]
    public void ANegativeAnswer_DoesNotSurviveARegistrationThatDeclaresThePage()
    {
        RecordPatches.ResetForReload();
        OpenTheGateFor(LateDeclaredPageId);

        var epochAtMiss = RecordPatches.BcAppRegistrationEpoch;

        // No registered .app declares this page yet, so no NCLMetaForm can be built for it
        // and the answer is null. This is also what records the negative answer.
        Assert.Null(RecordPatches.EnsureRealPageMetadata(LateDeclaredPageId));
        Assert.Equal(epochAtMiss, RecordPatches.PageRealMetadataNegativeEpochForTests(LateDeclaredPageId));
        Assert.Equal(0, RecordPatches.PageRealMetadataRetakesForTests(LateDeclaredPageId));

        RecordPatches.AddBcAppPath(WriteApp(PagesJson(LateDeclaredPageId, "PRM Late Page")));

        // Precondition, asserted rather than assumed: the registration really did move the
        // epoch, and the page really is declared now. Without both, the assertions below
        // could pass on a build that never revalidates anything.
        var epochAfterRegistration = RecordPatches.BcAppRegistrationEpoch;
        Assert.NotEqual(epochAtMiss, epochAfterRegistration);
        Assert.True(RecordPatches.HasDependencyPageMetadata(LateDeclaredPageId),
            "the .app just registered declares this page, so the dependency-metadata opt-in must see it");

        RecordPatches.EnsureRealPageMetadata(LateDeclaredPageId);

        Assert.Equal(1, RecordPatches.PageRealMetadataRetakesForTests(LateDeclaredPageId));
        Assert.NotEqual(epochAtMiss,
            RecordPatches.PageRealMetadataNegativeEpochForTests(LateDeclaredPageId) ?? epochAfterRegistration);
    }

    /// <summary>
    /// The bound, and the half that makes the test above non-vacuous: a retake happens at
    /// most ONCE per registration epoch. An implementation that simply dropped the negative
    /// record — or re-derived the answer on every call — passes the grow test and fails
    /// this one, and it would put BuildNCLMetaForm (which walks every registered .app's page
    /// list) on every TestPage construction.
    /// </summary>
    [Fact]
    public void ANegativeAnswer_IsRetakenAtMostOncePerRegistrationEpoch()
    {
        RecordPatches.ResetForReload();
        OpenTheGateFor(BoundedRetakePageId);

        Assert.Null(RecordPatches.EnsureRealPageMetadata(BoundedRetakePageId));
        RecordPatches.AddBcAppPath(WriteApp(PagesJson(BoundedRetakePageId, "PRM Bounded Page")));

        RecordPatches.EnsureRealPageMetadata(BoundedRetakePageId);
        Assert.Equal(1, RecordPatches.PageRealMetadataRetakesForTests(BoundedRetakePageId));

        // Four more asks, no registration in between: the answer for this epoch already
        // stands, whichever way it went.
        for (var i = 0; i < 4; i++) RecordPatches.EnsureRealPageMetadata(BoundedRetakePageId);
        Assert.Equal(1, RecordPatches.PageRealMetadataRetakesForTests(BoundedRetakePageId));
    }

    /// <summary>
    /// The same bound at the FIRST epoch, before anything has registered: repeated asks
    /// while the registration set holds still must not retake the question even once, and
    /// the stamp must stay put. This is the assertion an "always revalidate" implementation
    /// fails.
    /// </summary>
    [Fact]
    public void ANegativeAnswer_IsNotRetakenWhileTheRegistrationSetHoldsStill()
    {
        RecordPatches.ResetForReload();
        OpenTheGateFor(SteadyStatePageId);

        var epoch = RecordPatches.BcAppRegistrationEpoch;
        for (var i = 0; i < 5; i++) Assert.Null(RecordPatches.EnsureRealPageMetadata(SteadyStatePageId));

        Assert.Equal(0, RecordPatches.PageRealMetadataRetakesForTests(SteadyStatePageId));
        Assert.Equal(epoch, RecordPatches.PageRealMetadataNegativeEpochForTests(SteadyStatePageId));
    }

    // ── the negative arm: what must NOT be revalidated ───────────────────────

    /// <summary>
    /// A page NOTHING describes — no emit-captured XML, no dependency .app — never gets a
    /// negative record and is never retaken, however many .apps register afterwards.
    ///
    /// <para>This is the arm that stops the fix from becoming "retake everything". The vast
    /// majority of ids EnsureRealPageMetadata is asked about fall out at its opt-in gate;
    /// stamping and re-deriving each of those once per registered .app would put a walk of
    /// every registered .app's page list on a path that currently costs one dictionary
    /// lookup, and would grow the record without bound.</para>
    /// </summary>
    [Fact]
    public void APageNoSourceDescribes_IsNeverStampedAndNeverRetaken()
    {
        RecordPatches.ResetForReload();

        Assert.Null(RecordPatches.EnsureRealPageMetadata(NoMetadataSourcePageId));
        Assert.Null(RecordPatches.PageRealMetadataNegativeEpochForTests(NoMetadataSourcePageId));

        // An unrelated registration moves the epoch. The page still has no metadata source,
        // so it must still fall out at the gate.
        RecordPatches.AddBcAppPath(WriteApp(PagesJson(UnrelatedPageId, "PRM Unrelated Page")));
        Assert.NotEqual(NoMetadataSourcePageId, UnrelatedPageId);

        Assert.Null(RecordPatches.EnsureRealPageMetadata(NoMetadataSourcePageId));
        Assert.Null(RecordPatches.PageRealMetadataNegativeEpochForTests(NoMetadataSourcePageId));
        Assert.Equal(0, RecordPatches.PageRealMetadataRetakesForTests(NoMetadataSourcePageId));
    }

    // ── the SHRINK direction #1957 fixed, still intact ───────────────────────

    /// <summary>
    /// A bundle roll still discards the negative record. Redundant with the epoch on this
    /// path — ResetForReload bumps it through InvalidateBcAppIndexes — and asserted anyway,
    /// because the redundancy is the argument for keeping
    /// <c>ResetPageMetadataForReload</c>'s explicit clear and it should fail loudly if
    /// someone removes that clear on the strength of the epoch alone.
    /// </summary>
    [Fact]
    public void ANegativeAnswer_DoesNotSurviveABundleRoll()
    {
        RecordPatches.ResetForReload();
        OpenTheGateFor(SteadyStatePageId);

        Assert.Null(RecordPatches.EnsureRealPageMetadata(SteadyStatePageId));
        Assert.NotNull(RecordPatches.PageRealMetadataNegativeEpochForTests(SteadyStatePageId));

        RecordPatches.ResetForReload();

        Assert.Null(RecordPatches.PageRealMetadataNegativeEpochForTests(SteadyStatePageId));
        Assert.Equal(0, RecordPatches.PageRealMetadataRetakesForTests(SteadyStatePageId));
        Assert.False(RecordPatches.PageHasRealMetadataForTests(SteadyStatePageId));
    }

    // ── structural guard: ONE record, ONE guard, for BOTH negative answers ───

    /// <summary>
    /// The two negative answers must keep sharing one epoch-stamped record read by one
    /// guard.
    ///
    /// <para>This is a guard, not the proof — the proof is the behavioural tests above,
    /// which drive the "no NCLMetaForm could be built" branch. The catch branch cannot be
    /// driven from a test host without a usable in-process BC engine (LoadMetadata has to
    /// run and throw), and #3011's whole point is that a covered branch sitting next to an
    /// uncovered one is how this family of defects keeps recurring: #2478 was a reset that
    /// reset one half of a pair, #2888 instance 3 a second-order memo whose sibling was
    /// fixed and it was not. Holding both writes on one field read by one guard makes that
    /// divergence structurally impossible rather than merely unintended.</para>
    /// </summary>
    [Fact]
    public void BothNegativeAnswersShareOneEpochStampedRecordAndOneGuard()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "AlRunner", "Patches", "RecordPatches.RealPageMetadata.cs"));

        var code = string.Join('\n', source.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)
                     && !l.TrimStart().StartsWith("///", StringComparison.Ordinal)));

        // Scoped to EnsureRealPageMetadata's own body: the read-only test accessor above it
        // consults the same field and is not a guard.
        var body = MethodBody(code, "internal static object? EnsureRealPageMetadata", "private static void EvictPageMetaForm");
        Assert.Equal(1, CountOf(body, "_pageRealMetadataNegativeEpoch.TryGetValue("));
        Assert.True(CountOf(body, "_pageRealMetadataNegativeEpoch[") >= 2,
            "both negative answers — 'no NCLMetaForm could be built' and 'LoadMetadata threw' — must "
            + "stamp the SAME record, or one of them can be revalidated while the other is not (#3011)");

        // The pre-#3011 epoch-less failure set must not come back anywhere in the runner.
        var offenders = Directory
            .GetFiles(Path.Combine(RepoRoot(), "AlRunner"), "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("_pagesRealMetadataFailed", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Assert.True(offenders.Count == 0,
            "a negative real-page-metadata answer must carry the registration epoch it was taken at, "
            + "never live in an unstamped set that only a bundle roll clears (#3011): "
            + string.Join(", ", offenders));

        // #1957: the SUCCESS set is deliberately NOT epoch-stamped, so this clear is the
        // only thing that discards it when the NCLMetaForm instances it describes go.
        Assert.Contains("_pagesWithRealMetadata.Clear()", code, StringComparison.Ordinal);
    }

    /// <summary>The source between <paramref name="startMarker"/> and
    /// <paramref name="endMarker"/>. Both must be present exactly once, asserted — a marker
    /// that stopped matching after a rename would otherwise silently shrink the region to
    /// nothing and make every count assertion below it pass vacuously.</summary>
    private static string MethodBody(string code, string startMarker, string endMarker)
    {
        var start = code.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"expected to find \"{startMarker}\" in RecordPatches.RealPageMetadata.cs");
        var end = code.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"expected to find \"{endMarker}\" after \"{startMarker}\"");
        return code[start..end];
    }

    private static int CountOf(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }

    private static string RepoRoot() => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}
