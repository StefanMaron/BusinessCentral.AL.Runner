// BcAppRegistrationEpochInvalidationTests — issue #2888, the three instances of the
// "state derived from _bcAppPaths outlives a change to _bcAppPaths" family that #2944
// (_depPageMetadataXml / _depReportMetadataXml) deliberately left alone.
//
// The registration set moves in BOTH directions, through one funnel
// -----------------------------------------------------------------
//   * AddBcAppPath GROWS it — once per dependency .app, per bundle.
//   * ClearPerBundleBcAppPaths SHRINKS it — every --server request, every --watch cycle
//     (since #2755 / PR #2873).
// Both end in InvalidateBcAppIndexes(), which is where every derived index is dropped.
// The three pieces of state below were derived from the same set and were not dropped
// there.
//
// 1. RecordPatches._bcMissCache (RecordPatches.BcAppFallback.cs) — the negative cache
//    "no registered .app declares table N". Nothing cleared it, anywhere: not
//    AddBcAppPath, not ResetForReload. So a table id that missed under bundle 1 stayed
//    permanently missing for bundle 2..N of a --server process even though bundle 2's own
//    dependency declares it, and a miss taken between two AddBcAppPath calls in a single
//    run was never revisited. Covered by AMissedTableId_* below.
//
// 2. The seven generation keys of the form (_bcAppPaths.Count, ...). A COUNT is a sound
//    generation only while the list cannot shrink. It can, so a set that loses N entries
//    and gains N different ones reads as the same generation and the previous epoch's rows
//    are served — ABA, not staleness. The shape is proved behaviourally on
//    _depProcessingOnlyBuiltFrom (DependencyReportMetadata.cs), the one keyed on the bare
//    count with no other term to confound it; the remaining six are the identical
//    expression and are pinned structurally by NoGenerationKeyUsesTheAppPathCount below,
//    because their enumerators are private and reachable only through the virtual-table
//    provider path.
//
// 3. NavReportSync._realMetaCache — a second-order memo over the memo #2944 fixed.
//    BcRuntime.ResetForNewBundleReload clears it before ResetForReload, so the SHRINK
//    direction was already covered; the GROW direction was not, so a null memoized before
//    a registration masks the fix for that consumer. GetRealMetaReport then falls through
//    to the legacy stub that stamps ProcessingOnly, and BC refuses SaveAs — a wrong
//    refusal rather than a throw. Covered by ANullRealMetaReport_* below.
//
// Why these are mechanism tests and not corpus tests
// --------------------------------------------------
// Every claim here is about cache lifetime across a registration-set change inside ONE
// runner process — multi-bundle / server-mode wiring, which
// .claude/rules/bc-behavior-tests-go-upstream.md names as explicitly runner-specific. Real
// BC has no _bcMissCache, no _bcAppPaths and no MetaReport memo keyed on one; the AL a
// corpus test would run is identical on both sides of every fix here. Instance 3 was
// checked separately rather than inheriting that answer, because its symptom (SaveAs
// refused on a report that is not processing-only) IS AL-visible where #2944's was not:
// what AL cannot express is the runner-internal ordering that produces it — a
// GetRealMetaReport miss BEFORE the .app declaring the report is registered, inside one
// process. On a service tier the report metadata is simply there.
using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// RecordPatchesSerialCollection: this class calls RecordPatches.ResetForReload() directly,
// which ParserStaticsIsolationGuardTests requires, and it registers .apps (which argues for
// CacheRootsSerialCollection — a class can only join one). Same trade-off, and the same
// reasoning, as DependencyMetadataMemoInvalidationTests and
// DependencyReportProcessingOnlyTests: every .app written here is a fresh GUID-named file
// and BcAppSymbolCache's key is content-addressed, so the worst a concurrent CacheRoots
// override can do is turn a HIT into a MISS and re-parse the same bytes to the same result.
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class BcAppRegistrationEpochInvalidationTests : IDisposable
{
    private readonly string _root;

    public BcAppRegistrationEpochInvalidationTests()
    {
        _root = TestScratch.Dir("al-runner-2888-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // Ids process-wide unique among AlRunner.Tests statics — _bcAppPaths, _parsedTables and
    // both report memos are process-global and nothing in the test host unregisters an .app.
    // Neighbouring blocks in use: 881234xx and 881235xx (DependencyPageMetadataXmlTests),
    // 881235xx (DependencyMetadataMemoInvalidationTests, disjoint literals), 881236xx
    // (DependencyReportProcessingOnlyTests), 881237xx (TableRelationWhereFieldLinkTests).
    // This file owns 881238xx.
    private const int UnrelatedTableId = 88123801;
    private const int LateTableId = 88123802;
    private const int NeverDeclaredTableId = 88123803;
    private const int AbaReportId = 88123850;
    private const int UnrelatedReportId = 88123851;
    private const int LateRealMetaReportId = 88123852;

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

    /// <summary>Top-level "Tables" container, the shape BcAppSymbolCache parses and
    /// SymbolAppFixture.SymbolReferenceForTable writes.</summary>
    private static string TablesJson(int tableId, string tableName, params (int Id, string Name)[] fields)
    {
        var sb = new StringBuilder();
        sb.Append("{ \"RuntimeVersion\": \"15.1\", \"Tables\": [ {");
        sb.Append($" \"Id\": {tableId}, \"Name\": \"{tableName}\", \"Fields\": [");
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($" {{ \"Id\": {fields[i].Id}, \"Name\": \"{fields[i].Name}\", \"TypeDefinition\": {{ \"Name\": \"Integer\" }} }}");
        }
        sb.Append("] } ] }");
        return sb.ToString();
    }

    /// <summary>Namespaced "Reports" container, the shape DependencyReportProcessingOnlyTests
    /// writes. <paramref name="processingOnly"/> null omits the property entirely, which is
    /// how the vast majority of real Base App reports look.</summary>
    private static string ReportsJson(int reportId, string name, bool? processingOnly)
    {
        var props = $"{{ \"Name\": \"Caption\", \"Value\": \"{name}\" }}";
        if (processingOnly is bool po)
            props += $", {{ \"Name\": \"ProcessingOnly\", \"Value\": \"{(po ? 1 : 0)}\" }}";
        return $$"""
            {
              "RuntimeVersion": "15.1",
              "Namespaces": [ { "Name": "BARE", "Reports": [
                { "Id": {{reportId}}, "Name": "{{name}}", "Properties": [ {{props}} ] }
              ] } ]
            }
            """;
    }

    // ── 1. _bcMissCache ──────────────────────────────────────────────────────

    /// <summary>
    /// The negative cache must not outlive a registration that ADDS the .app declaring the
    /// table it recorded a miss for.
    ///
    /// <para>Driven through <c>TryResolveDependencyFieldId</c> — a real production consumer
    /// (#2467, resolving a dependency part's SubPageLink field names to numbers), not a
    /// test-only probe. Its first act on a _parsedTables miss is
    /// TryPopulateParsedTableFromBcApps, whose first act is the _bcMissCache check.</para>
    ///
    /// <para>Both polarities are here. The negative arm matters most: without it a "fix"
    /// that simply stopped consulting the negative cache — or one that answered a field id
    /// for anything asked about — would pass.</para>
    /// </summary>
    [Fact]
    public void AMissedTableId_IsResolvableAfterALaterAppDeclaresIt()
    {
        RecordPatches.AddBcAppPath(WriteApp(TablesJson(UnrelatedTableId, "BARE Unrelated", (1, "Entry No."))));

        // Precondition AND the thing that memoizes the miss. Asserted rather than assumed:
        // if another test had already registered an .app declaring this id, the assertion
        // after the registration below could pass without the fix.
        Assert.Null(RecordPatches.TryResolveDependencyFieldId(LateTableId, "Payload"));

        RecordPatches.AddBcAppPath(WriteApp(TablesJson(
            LateTableId, "BARE Late Table", (1, "Entry No."), (42, "Payload"))));

        // The concrete value, not merely "not null": 42 is the field id the second .app's
        // symbol file states, so a fix that resolved the table but lost the field mapping
        // cannot pass either.
        Assert.Equal(42, RecordPatches.TryResolveDependencyFieldId(LateTableId, "Payload"));
        Assert.Equal(1, RecordPatches.TryResolveDependencyFieldId(LateTableId, "Entry No."));

        // Negative arm 1: a field that table does not declare stays unresolvable.
        Assert.Null(RecordPatches.TryResolveDependencyFieldId(LateTableId, "No Such Field"));
        // Negative arm 2: a table no registered .app declares at all stays unresolvable —
        // dropping the negative cache must not turn every miss into an answer.
        Assert.Null(RecordPatches.TryResolveDependencyFieldId(NeverDeclaredTableId, "Payload"));
    }

    /// <summary>
    /// The same negative cache across a bundle roll. This is the route that is live on
    /// <c>main</c> with no assumption about intra-run call ordering: ResetForReload
    /// unregisters bundle 1's .apps and clears _parsedTables, so every miss bundle 1 took
    /// has to be retaken against bundle 2's registrations — and nothing cleared the miss
    /// cache on that path either.
    ///
    /// <para>The roll is followed by a registration because that is what Program.cs does
    /// (ResetForNewBundleReload at 4049, AddBcAppPath at 4578); the discriminating fact is
    /// that bundle 1 asked about this id and got a miss BEFORE the roll.</para>
    /// </summary>
    [Fact]
    public void AMissedTableId_DoesNotSurviveTheBundleRollIntoABundleThatDeclaresIt()
    {
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(WriteApp(TablesJson(UnrelatedTableId, "BARE Unrelated", (1, "Entry No."))));

        const int RollTableId = 88123804;
        Assert.Null(RecordPatches.TryResolveDependencyFieldId(RollTableId, "Payload"));

        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(WriteApp(TablesJson(
            RollTableId, "BARE Rolled Table", (1, "Entry No."), (7, "Payload"))));

        Assert.Equal(7, RecordPatches.TryResolveDependencyFieldId(RollTableId, "Payload"));
    }

    // ── 2. the count-keyed generations ───────────────────────────────────────

    /// <summary>
    /// ABA on a generation keyed by <c>_bcAppPaths.Count</c>: one .app out, one different
    /// .app in, same count, contents changed — and the memo built from the first is served
    /// for the second.
    ///
    /// <para><c>_depProcessingOnlyBuiltFrom</c> is the site keyed on the BARE count, with no
    /// second tuple term that could accidentally rescue the comparison, so it is where the
    /// shape is provable rather than argued. The other six sites are the same expression
    /// inside a tuple; <see cref="NoGenerationKeyUsesTheAppPathCount"/> pins them.</para>
    ///
    /// <para>Both reads deliberately happen at the SAME registered-app count. The leading
    /// ResetForReload establishes the baseline so the two AddBcAppPath calls land on
    /// identical counts; without it the test would be measuring whatever count previous
    /// tests in this process left behind, and could pass by accident.</para>
    ///
    /// <para>The answer flips true → false, which is the direction that costs something: a
    /// report wrongly reported processing-only gets no layout attempt at all.</para>
    /// </summary>
    [Fact]
    public void ADependencyProcessingOnlyAnswer_DoesNotSurviveASameCountSwapOfTheRegisteredApps()
    {
        RecordPatches.ResetForReload();
        var countBefore = RecordPatches.RegisteredBcAppPathsForTests().Count;

        RecordPatches.AddBcAppPath(WriteApp(ReportsJson(AbaReportId, "BARE Aba Report", processingOnly: true)));
        Assert.True(RecordPatches.IsDependencyReportProcessingOnly(AbaReportId),
            "the registered .app states ProcessingOnly = 1, so this must be true — and it is what memoizes the set");

        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(WriteApp(ReportsJson(AbaReportId, "BARE Aba Report", processingOnly: null)));

        // The ABA precondition, asserted rather than assumed: if the two registrations did
        // not land on the same count, the assertion below would pass on the broken build
        // too and prove nothing.
        Assert.Equal(countBefore + 1, RecordPatches.RegisteredBcAppPathsForTests().Count);

        // AL's default for a report that states no ProcessingOnly is false, and the only
        // registered .app now states none.
        Assert.False(RecordPatches.IsDependencyReportProcessingOnly(AbaReportId),
            "the only registered .app declares no ProcessingOnly, so the answer must be AL's default");

        // And the rebuild is a real rebuild, not an empty set: a third same-count swap back
        // to a declaring .app answers true again.
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(WriteApp(ReportsJson(AbaReportId, "BARE Aba Report", processingOnly: true)));
        Assert.True(RecordPatches.IsDependencyReportProcessingOnly(AbaReportId));

        // Negative arm: a report no registered .app declares is not processing-only.
        Assert.False(RecordPatches.IsDependencyReportProcessingOnly(88123859));
    }

    /// <summary>
    /// The SHRINK direction of the same generation key, isolated. The assertion sits BETWEEN
    /// the roll and the next registration, deliberately: assert after re-registering and the
    /// test passes on a build that only bumps the epoch on the GROW path, which is the half
    /// <see cref="ADependencyProcessingOnlyAnswer_DoesNotSurviveASameCountSwapOfTheRegisteredApps"/>
    /// and <see cref="AMissedTableId_IsResolvableAfterALaterAppDeclaresIt"/> already cover.
    /// (Same trap, same reasoning, as DependencyMetadataMemoInvalidationTests' third test.)
    ///
    /// <para>Immediately after a roll no per-bundle .app is registered at all, so the only
    /// honest answer for a report declared solely by bundle 1's .app is AL's default — and a
    /// generation the roll did not move answers with bundle 1's set instead.</para>
    /// </summary>
    [Fact]
    public void ADependencyProcessingOnlyAnswer_DoesNotSurviveTheRollThatUnregistersItsApp()
    {
        const int RollOnlyReportId = 88123853;

        RecordPatches.ResetForReload();
        var app = WriteApp(ReportsJson(RollOnlyReportId, "BARE Roll Only", processingOnly: true));
        RecordPatches.AddBcAppPath(app);
        Assert.True(RecordPatches.IsDependencyReportProcessingOnly(RollOnlyReportId),
            "the registered .app states ProcessingOnly = 1 — this is the read that memoizes the set");

        RecordPatches.ResetForReload();

        // Precondition, asserted rather than assumed: the roll really did unregister the
        // .app. Without it, a false below could mean the memo was rebuilt OR that the .app
        // was never registered, and only the first is the claim.
        Assert.DoesNotContain(
            RecordPatches.RegisteredBcAppPathsForTests(),
            p => string.Equals(p, app, StringComparison.OrdinalIgnoreCase));

        Assert.False(RecordPatches.IsDependencyReportProcessingOnly(RollOnlyReportId),
            "no .app declaring this report is registered any more, so the answer must be AL's default");
    }

    /// <summary>
    /// Structural guard for the six remaining count-keyed generations, whose enumerators are
    /// private and reachable only through the virtual-table provider path. Every one of them
    /// now keys on the monotonic registration epoch instead, which cannot ABA.
    ///
    /// <para>This is a guard, not the proof — the proof is the test above, on the seventh
    /// site with the identical expression. What this adds is that a future
    /// <c>_bcAppPaths.Count</c> reintroduced into a generation key fails immediately rather
    /// than waiting for someone to hit the ABA in a --watch session.</para>
    /// </summary>
    [Fact]
    public void NoGenerationKeyUsesTheAppPathCount()
    {
        var patchesDir = Path.Combine(RepoRoot(), "AlRunner", "Patches");
        Assert.True(Directory.Exists(patchesDir), $"expected the patches directory at {patchesDir}");

        var offenders = new List<string>();
        var files = Directory.GetFiles(patchesDir, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!line.Contains("_bcAppPaths.Count", StringComparison.Ordinal)) continue;
                // A generation key is an ASSIGNMENT of the count into a comparison value.
                // Diagnostic interpolations that merely REPORT how many .apps were scanned
                // are fine and stay (two of them exist, in the trace lines of the table- and
                // report-metadata builders).
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("///", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal))
                    continue;
                if (!line.Contains("generation", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("BuiltFrom", StringComparison.Ordinal))
                    continue;
                offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {trimmed}");
            }
        }

        Assert.True(offenders.Count == 0,
            "a cache generation must key on RecordPatches' registration EPOCH, never on _bcAppPaths.Count — "
            + "a count cannot distinguish a set that lost N entries and gained N different ones (#2888):"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    // ── 3. NavReportSync._realMetaCache ──────────────────────────────────────

    /// <summary>
    /// The second-order memo: a null MetaReport memoized before the .app declaring that
    /// report was registered must not survive the registration.
    ///
    /// <para>Observed through GetRealMetaReport's own stderr, which distinguishes the two
    /// states exactly. With no metadata available anywhere the factory returns null at its
    /// first branch and says nothing; with metadata available it always names the report id
    /// — "built REAL MetaReport for report N" when the BC Types assembly is present in the
    /// host, "real MetaReport build FAILED for report N" when it is not. So a second call
    /// that produces NO line naming the id is a served memo, on either kind of host.</para>
    ///
    /// <para>The underlying dependency metadata is asserted non-null between the two calls,
    /// so a green here cannot mean the second .app was unreadable and the answer was null
    /// for an honest reason.</para>
    /// </summary>
    [Fact]
    public void ANullRealMetaReport_DoesNotSurviveALaterAppDeclaringThatReport()
    {
        RecordPatches.AddBcAppPath(WriteApp(ReportsJson(UnrelatedReportId, "BARE Unrelated Report", null)));

        string? first = null;
        var beforeStderr = CaptureStderr(() => first = ObjectOrNull(() => NavReportSync.GetRealMetaReport(LateRealMetaReportId)));
        Assert.Null(first);
        Assert.DoesNotContain($"report {LateRealMetaReportId}", beforeStderr);

        RecordPatches.AddBcAppPath(WriteApp(ReportsJson(LateRealMetaReportId, "BARE Late Real Meta", null)));

        // Positive control: the memo #2944 fixed answers for this id now, so metadata IS
        // available to the factory this test is about.
        Assert.NotNull(RecordPatches.TryBuildDependencyReportMetadata(LateRealMetaReportId));

        var afterStderr = CaptureStderr(() => NavReportSync.GetRealMetaReport(LateRealMetaReportId));
        Assert.Contains($"MetaReport", afterStderr);
        Assert.Contains($"report {LateRealMetaReportId}", afterStderr);
    }

    /// <summary>Reduce a MetaReport (or null) to a marker string, so the assertion above
    /// never depends on the BC Types assembly being loadable in the test host.</summary>
    private static string? ObjectOrNull(Func<object?> f) => f() == null ? null : "built";

    private static string CaptureStderr(Action action)
    {
        var saved = Console.Error;
        var buffer = new StringWriter();
        try
        {
            Console.SetError(buffer);
            action();
        }
        finally
        {
            Console.SetError(saved);
        }
        return buffer.ToString();
    }

    // Same locator BaseAppFloorFixtureGuardTests uses for the same reason: the test binary
    // sits at <repo>/AlRunner.Tests/bin/<config>/<tfm>/.
    private static string RepoRoot() => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}
