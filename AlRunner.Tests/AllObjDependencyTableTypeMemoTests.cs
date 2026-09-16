// AllObjDependencyTableTypeMemoTests — issue #4225, the `table` arm of
// DependencyObjectSubtype behind AllObjWithCaption (2000000058).
//
// What was wrong
// --------------
// _aovDependencyTableTypes was memoized on NOTHING — `null` versus non-`null`. It was
// populated on first use and then survived for the life of the process: not cleared by
// InvalidateBcAppIndexes, not by ClearPerBundleBcAppPaths, and not by ResetForReload, which
// clears _metaTableCache and its siblings but never reached this field. So the FIRST
// registration epoch's table subtypes were replayed for every later one — every --server
// request after the first, and every --watch reload.
//
// That is a CORRECTNESS defect, not a cost one: AllObjWithCaption."Object Subtype" answered a
// value that no registered .app declares. It is the #2888 family ("state derived from
// _bcAppPaths outlives a change to _bcAppPaths") in its strongest form — #2888 fixed the seven
// generation keys spelled as _bcAppPaths.Count, and NoGenerationKeyUsesTheAppPathCount pins
// that structurally, but that guard only inspects lines containing _bcAppPaths.Count, so a memo
// with NO generation term at all was invisible to it (#4227 tracks widening the guard).
//
// What these tests pin
// --------------------
// The lifetime of the memo across a registration-set change, and that memoizing changed no
// answer. Every test asserts a CONCRETE subtype string in each epoch — "Temporary" versus
// "CRM" versus "Normal" — so a green can never mean "the second .app was unreadable and null
// was an honest answer", and a memo that merely returned some non-null value cannot pass.
//
// Why these are mechanism tests and not corpus tests
// --------------------------------------------------
// Every claim here is about how long a runner-internal memo lives inside ONE process across a
// change to the registered .app set. Real BC has no _bcAppPaths, no registration epoch and no
// --server reload: a service tier serves one application at a time and has nothing that could
// go stale in this way, so there is no AL a corpus test could run that would distinguish the
// fixed runner from the broken one. The BC-behaviour claim these values rest on — that
// AllObjWithCaption."Object Subtype" carries a table's TableType, and that a table declaring
// none reports "Normal" — is already adjudicated upstream by codeunit 60802 "Test AllObj
// Virtual Table" (corpus PR StefanMaron/BusinessCentral.AL.Language.Tests#264) and by
// Record_TableMetadata_Get_DeclaredTable_ReturnsMatchingRow, both green on a real service tier.
// Same reasoning, and the same conclusion, as DependencyPageSymbolIndexMemoTests (#3774) and
// BcAppRegistrationEpochInvalidationTests (#2888), the two neighbours this suite joins.
using System.IO.Compression;
using System.Reflection;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// RecordPatchesSerialCollection: this class calls RecordPatches.ResetForReload() and registers
// .apps, moving _bcAppPaths and the registration epoch — state ParserStaticsIsolationGuardTests
// requires to be serialized. A concurrent class registering its own .app would move the epoch
// underneath these assertions and turn a staleness claim into a flake. Same trade-off, and the
// same reasoning, as DependencyPageSymbolIndexMemoTests: every .app written here is a fresh
// GUID-named file and BcAppSymbolCache's key is content-addressed, so the worst a concurrent
// CacheRoots override can do is turn a HIT into a MISS and re-parse the same bytes to the same
// result.
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class AllObjDependencyTableTypeMemoTests : IDisposable
{
    private readonly string _root;

    public AllObjDependencyTableTypeMemoTests()
    {
        _root = TestScratch.Dir("al-runner-4225-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // Ids process-wide unique among AlRunner.Tests statics — _bcAppPaths and _parsedTables are
    // process-global and nothing in the test host unregisters an .app. Neighbouring blocks in
    // use: 881234xx/881235xx (DependencyPageMetadataXmlTests), 881236xx
    // (DependencyReportProcessingOnlyTests), 881237xx (TableRelationWhereFieldLinkTests),
    // 881238xx (BcAppRegistrationEpochInvalidationTests), 881239xx
    // (PageRealMetadataEpochRevalidationTests), 881240xx (ObjectIndexMemoTests), 881241xx
    // (DependencyPageSymbolIndexMemoTests). This file owns 881242xx.
    private const int SwapTableId = 88124201;
    private const int FirstTableId = 88124202;
    private const int SecondTableId = 88124203;
    private const int LateTableId = 88124204;
    private const int NeverDeclaredTableId = 88124205;
    private const int ContestedTableId = 88124206;
    private const int SoleTableId = 88124207;

    private static readonly Type T = typeof(RecordPatches);

    private static string? Subtype(int tableId) =>
        (string?)T.GetMethod("DependencyTableTypeName",
            BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object?[] { tableId });

    /// <summary>
    /// One .app declaring the given tables, each with the given <c>TableType</c>. A null
    /// tableType writes NO TableType property at all, which is how a table that declares none
    /// reaches the parser — the case whose answer is the <c>AlDefaultTableType</c> fallback.
    /// </summary>
    private string WriteApp(params (int Id, string Name, string? TableType)[] tables)
    {
        var sb = new StringBuilder();
        sb.Append("{ \"RuntimeVersion\": \"15.1\", \"Namespaces\": [ { \"Name\": \"Issue4225\", \"Tables\": [");
        for (var i = 0; i < tables.Length; i++)
        {
            if (i > 0) sb.Append(',');
            var props = tables[i].TableType is { } tt
                ? $$"""{ "Name": "TableType", "Value": "{{tt}}" }"""
                : "";
            sb.Append($$"""
                 { "Id": {{tables[i].Id}}, "Name": "{{tables[i].Name}}",
                   "Properties": [ {{props}} ],
                   "Fields": [ { "Id": 1, "Name": "Code", "TypeDefinition": { "Name": "Code",
                       "Subtype": { "Name": "20" } } } ] }
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
    /// THE ISSUE (#4225). A same-COUNT swap of the registered set must be observed: the memo
    /// may not replay the previous epoch's subtype.
    ///
    /// <para>Both reads happen at the same registered-app count — one .app is registered, the
    /// set is cleared, and a DIFFERENT .app declaring the SAME table id with a DIFFERENT
    /// TableType is registered. This is the exact shape the issue's probe measured, and the
    /// shape a count-keyed memo could not see either (#2888's ABA case): only the monotonic
    /// registration epoch distinguishes the two.</para>
    ///
    /// <para>The answer flips Normal → Temporary, a concrete word in each epoch, so a green
    /// cannot mean "the second .app was unreadable and null was an honest answer". Both
    /// directions are asserted — the stale value is named as the thing that must NOT come
    /// back, so a memo answering the first epoch forever fails with Expected Temporary /
    /// Actual Normal rather than merely "not null".</para>
    /// </summary>
    [Fact]
    public void ASameCountSwapOfTheRegisteredApps_IsNotServedFromTheMemo()
    {
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(WriteApp((SwapTableId, "Issue4225 Swap", null)));

        // Epoch A: the table declares no TableType, so AL's default answers.
        Assert.Equal("Normal", Subtype(SwapTableId));

        // Same count on both sides: exactly one registered .app before and after.
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(WriteApp((SwapTableId, "Issue4225 Swap", "Temporary")));

        // Epoch B: a DIFFERENT .app declaring the SAME id as Temporary.
        Assert.Equal("Temporary", Subtype(SwapTableId));
    }

    /// <summary>
    /// The GROW direction: a table declared by an .app registered AFTER the memo was built must
    /// be found, and the entries already in the memo must survive the rebuild.
    ///
    /// <para>This is the direction a --watch reload that ADDS a dependency takes, and the one
    /// that turns a stale memo into a null rather than into a wrong word: DependencyObjectSubtype
    /// reads this as <c>"table" =&gt; DependencyTableTypeName(o.Id)</c>, so a memo built before the
    /// late registration answers null for a table the runner does now know about, and
    /// AllObjWithCaption reports a blank subtype for it.</para>
    ///
    /// <para>The absence is asserted BEFORE the late registration rather than assumed: without
    /// that, a table left registered by an earlier test in the same process would make this
    /// pass without invalidating anything.</para>
    /// </summary>
    [Fact]
    public void ATableDeclaredByAnAppRegisteredAfterTheMemoWasBuilt_IsStillFound()
    {
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(WriteApp((FirstTableId, "Issue4225 First", "CRM")));

        // Build the memo at an epoch that does NOT know about the late table.
        Assert.Equal("CRM", Subtype(FirstTableId));
        Assert.Null(Subtype(LateTableId));

        RecordPatches.AddBcAppPath(WriteApp((LateTableId, "Issue4225 Late", "Temporary")));

        Assert.Equal("Temporary", Subtype(LateTableId));
        // The earlier entry survives the rebuild — invalidation must rebuild, never truncate.
        Assert.Equal("CRM", Subtype(FirstTableId));
    }

    /// <summary>
    /// The SHRINK direction: a table that the incoming registration set no longer declares must
    /// stop answering. A memo that only ever grew would keep reporting bundle 1's tables to
    /// bundle 2 — which is #4222's shape, and the reason ClearPerBundleBcAppPaths exists.
    ///
    /// <para>Null, not the empty string: <c>DependencyObjectSubtype</c> returns null for "this
    /// kind has no subtype" too, and <c>ObjectSubtypeTextFor</c> is what turns that into the
    /// blank BC reports. A stale non-null here would be a subtype for an object the runner no
    /// longer knows exists.</para>
    /// </summary>
    [Fact]
    public void ATableTheIncomingRegistrationNoLongerDeclares_StopsAnswering()
    {
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(WriteApp((FirstTableId, "Issue4225 First", "CRM")));
        Assert.Equal("CRM", Subtype(FirstTableId));

        // A new bundle registering an .app that declares a DIFFERENT table entirely.
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(WriteApp((SecondTableId, "Issue4225 Second", "Temporary")));

        Assert.Equal("Temporary", Subtype(SecondTableId));
        Assert.Null(Subtype(FirstTableId));
    }

    /// <summary>
    /// The answers are unchanged by memoizing, for tables in different .apps, for a table
    /// declaring no TableType, and for a table nothing declares — repeated across the memo
    /// boundary so the served answer is checked as well as the built one.
    ///
    /// <para>Three DISTINCT subtype words, so a memo keyed or merged wrongly hands out the
    /// wrong one and is caught; a non-null check could not see that. The words are asserted to
    /// differ first, so a fixture that accidentally gave every table the same TableType could
    /// not make this pass vacuously.</para>
    /// </summary>
    [Fact]
    public void AnswersAreIdentical_AcrossTheMemoBoundary()
    {
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(WriteApp((FirstTableId, "Issue4225 First", "CRM")));
        RecordPatches.AddBcAppPath(WriteApp(
            (SecondTableId, "Issue4225 Second", "Temporary"),
            (LateTableId, "Issue4225 Plain", null)));

        // Distinct by construction — pin it, so the discrimination below is real.
        Assert.NotEqual("CRM", "Temporary");
        Assert.NotEqual("Temporary", "Normal");

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal("CRM", Subtype(FirstTableId));
            Assert.Equal("Temporary", Subtype(SecondTableId));
            // Declares no TableType — AL's default, the word BC reports for a table (it is
            // blanked for a CODEUNIT only; see AllObjWithCaptionObjectSubtypeTests).
            Assert.Equal("Normal", Subtype(LateTableId));
            // A table no registered .app declares stays a truthful null — never a stale hit.
            Assert.Null(Subtype(NeverDeclaredTableId));
        }
    }

    /// <summary>
    /// The ORDERING claim: when two registered .apps both declare the same table id, the
    /// LAST-registered one wins.
    ///
    /// <para>Note this is the OPPOSITE of DependencyPageSymbolsById's first-wins rule, and it
    /// is deliberate on both sides: each reproduces the precedence its own pre-memo code had.
    /// The page walk returned on its first match; this one has always built a dictionary with
    /// the INDEXER (<c>map[t.TableId] = ...</c>), whose last write wins. Preserving it keeps
    /// this change a pure memoization rather than a silent behaviour change riding along with
    /// an invalidation fix.</para>
    ///
    /// <para>Reachable, not hypothetical: AddBcAppPath dedupes on the PATH only, so two
    /// different .app FILES may each declare table N and which one answers is a real
    /// observable.</para>
    ///
    /// <para>Nothing else in this suite can see the property — every other test gives each
    /// .app its own table ids, so switching the indexer to TryAdd leaves all of them green.
    /// The two words differ by construction and the assertion names the expected one
    /// concretely, so a reversal answers Expected CRM / Actual Temporary rather than merely
    /// "not null".</para>
    /// </summary>
    [Fact]
    public void WhenTwoAppsDeclareTheSameTableId_TheLastRegisteredWins()
    {
        RecordPatches.ResetForReload();

        // Two DISTINCT .app files — WriteApp gives each a fresh GUID name, so the path-keyed
        // dedupe at AddBcAppPath registers both rather than collapsing them.
        var firstRegistered = WriteApp((ContestedTableId, "Issue4225 Contested First", "Temporary"));
        var secondRegistered = WriteApp((ContestedTableId, "Issue4225 Contested Second", "CRM"));
        Assert.NotEqual(firstRegistered, secondRegistered);

        RecordPatches.AddBcAppPath(firstRegistered);
        RecordPatches.AddBcAppPath(secondRegistered);

        Assert.Equal("CRM", Subtype(ContestedTableId));
        // Repeat across the memo boundary: the served answer must carry the same precedence as
        // the build did, not merely happen to be right on the pass that built the index.
        Assert.Equal("CRM", Subtype(ContestedTableId));
    }

    /// <summary>
    /// THE CONTROL for the swap test above. Same mechanism, same calls, but only ONE .app ever
    /// declares this id and its TableType never changes — so there is no stale value available
    /// to replay and the assertion must hold whether or not the memo is invalidated.
    ///
    /// <para>This is what makes the swap test's RED attributable. A test that reddens when the
    /// epoch key is removed proves only that it reacts to SOMETHING; pairing it with a case
    /// where the property does not apply, and requiring that case to stay GREEN under the same
    /// mutation, is what shows it is reacting to staleness rather than to the reset, the
    /// re-registration, or the fixture. Measured when the epoch key was removed: the swap test
    /// answers Expected Temporary / Actual Normal while this one stays GREEN.</para>
    ///
    /// <para>The first read is made to REBUILD rather than merely to read, which is what makes
    /// this a control at all. The memo is process-global and this class runs its tests in one
    /// process, so on a broken runner a sibling test's map is already published when this test
    /// starts and this .app would never be walked — the read would answer null and this test
    /// would red for a reason that has nothing to do with the property it exists to isolate.
    /// Registering the .app and asserting a HIT on its own id before the churn is what rules
    /// that out: on a broken runner the assert below fails first, loudly, instead of the
    /// control silently becoming order-dependent. (It genuinely did, before that assert
    /// existed: run alone it passed under the mutation, run with the class it failed.)</para>
    /// </summary>
    [Fact]
    public void ATableOnlyOneAppEverDeclares_IsUnaffectedByTheRegistrationChurn()
    {
        // Force a rebuild that this test's own .app takes part in, regardless of what any
        // sibling test left in the process-global memo — see the summary above.
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(WriteApp((SoleTableId, "Issue4225 Sole", "Temporary")));
        Assert.Equal("Temporary", Subtype(SoleTableId));

        // The same churn the swap test performs, but the incoming .app re-declares the id with
        // the SAME TableType, so no epoch disagrees with any other about this table. A memo
        // that never invalidates replays "Temporary", and a memo that does invalidate rebuilds
        // to "Temporary" — the two implementations are indistinguishable here, which is the
        // whole point.
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(WriteApp((SoleTableId, "Issue4225 Sole", "Temporary")));

        Assert.Equal("Temporary", Subtype(SoleTableId));
    }
}
