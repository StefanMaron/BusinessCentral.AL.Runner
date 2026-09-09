// TestPartPageIdAndSearchStartTests — the C# half of issue #3312.
//
// The AL-observable claims (TestPart.GoToKey lands on the keyed row; FindFirstField finds the
// earliest match; FindNextField advances; FindPreviousField goes back) are claims about BC, so
// they do not belong here. They are measured against a real service tier by corpus codeunit
// 60346 "Test TestPart", merged as StefanMaron/BusinessCentral.AL.Language.Tests#227 — six
// tests that fail on the runner before this fix and pass after it.
//
// What this file pins is the two runner-side mechanisms that fix depends on, both of which are
// invisible to the corpus because the corpus can only see the AL answer, not which of two
// plausible implementations produced it.
//
// ── Mechanism 1: a part's page id does not come from pageUnderTestId ────────────────────────
//
// BC hardcodes page id 0 for every part. Microsoft.Dynamics.Nav.Ncl.dll, NavTestPart's only
// constructor (get_members_of_type reports exactly one):
//
//     internal NavTestPart(ITreeObject parent, ITestPart testPart)
//         : base(parent, new ApplicationObjectId(ObjectType.Page, 0), testPart)
//
// so pageUnderTestId.ObjectNumber is 0 by construction, never because anything is unknown.
// BC's own base constructor agrees it is an expected value rather than an error
// (`if (pageUnderTestId.ObjectNumber != 0) metaPage = LoadMetadata();`), and NavTestPart then
// resolves its metadata from the OTHER field — NavTestPart.LoadMetadata() is
// `Tree.Session.MetadataProvider.GetPageDefinition(testPart.PageId)`. So BC's own answer to
// "which page is this part" is ITestPage.PageId off the client object.
//
// Not version-specific: the NavTestPart constructor decompiles to the byte-identical hash
// 6c0201fa8b48b1cd7880446ce4605fefc1e1d58449d795516d3f9ed2fa5415fd on both BC 27.0 and 28.1,
// and compare_symbols across the type reports no added, removed or changed members between
// them.
//
// ── Mechanism 2: FindRowFromControlFieldValue resumes, FindRowFromTableFieldValues restarts ─
//
// The two ITestPage search entry points are NOT interchangeable, because their BC callers
// position the cursor differently before calling them:
//
//   InternalFindRowFromControlFieldValue (FindFirstField/FindNextField/FindPreviousField)
//     switch (initialMove) {
//       case InitialMove.First:    TestPage.MoveFirst(); break;
//       case InitialMove.Next:     if (!TestPage.MoveNext())     return false; break;
//       case InitialMove.Previous: if (!TestPage.MovePrevious()) return false; break;
//     }
//     return TestPage.FindRowFromControlFieldValue(fieldNo, value, initialMove != InitialMove.Previous);
//
//   InternalFindRowFromTableFieldValues (GoToKey/GoToRecord)
//     TestPage.MoveFirst();
//     bool num = TestPage.FindRowFromTableFieldValues(fieldNos, values, forward);
//
// The second unconditionally seeks to the first row, so for THAT caller "scan the whole
// rowset" and "resume from the cursor" give the same answer — which is why the runner's
// always-restart scan was correct for GoToRecord (#2537, tests/runner-extras
// testpage-gotorecord) and stayed that way. The first deliberately positions elsewhere, and
// re-seeking discards it: FindNextField then re-finds the row FindFirstField already returned
// and never advances, FindPreviousField likewise never goes back.
//
// Verified against Microsoft.Dynamics.Nav.Ncl.dll via the bc-decompiler tools.
using System;
using System.IO;
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPartPageIdAndSearchStartTests
{
    private const int PartPageId = 60342;

    // ── Mechanism 1 ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The client object a part is built over must name the part's OWN page. This is the
    /// value BC's NavTestPart.LoadMetadata reads (testPart.PageId), and — since #3312 — the
    /// value CodeunitPatches.GetPageIdFromTestPage falls back to when pageUnderTestId reads 0.
    /// A part answering 0 here is what made GetMetaTable refuse "TestPage 0".
    /// </summary>
    [Fact]
    public void LiveNavTestPart_ReportsThePartPagesOwnId_NotZero()
    {
        var part = Part(PartPageId);

        Assert.Equal(PartPageId, part.PageId);
        Assert.NotEqual(0, part.PageId);
    }

    /// <summary>
    /// PageId must be a real ITestPage member, not a runner-only convenience: the fallback
    /// reads it through the interface off a field typed as ITestPage, so a rename or a
    /// runner-local-only declaration would silently stop resolving.
    /// </summary>
    [Fact]
    public void PageId_IsDeclaredOnBcsOwnITestPageInterface()
    {
        var declared = typeof(Microsoft.Dynamics.Nav.Types.ITestPage)
            .GetProperty("PageId", BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(declared);
        Assert.Equal(typeof(int), declared!.PropertyType);
        // And the runner's part really is an ITestPage, so the fallback's type test matches.
        Assert.IsAssignableFrom<Microsoft.Dynamics.Nav.Types.ITestPage>(Part(PartPageId));
    }

    /// <summary>
    /// The fallback is narrow ON PURPOSE. A part whose client cannot name its page still
    /// yields 0, which leaves GetMetaTable's refusal intact rather than substituting a guess —
    /// the #2341 hole that refusal exists to plug. The mock client is exactly that case.
    /// </summary>
    [Fact]
    public void AClientThatCannotNameItsPage_StillAnswersZero_SoTheRefusalSurvives()
    {
        Assert.Equal(0, new MockITestPart().PageId);
    }

    /// <summary>
    /// The two readers of the page id in CodeunitPatches — GetMetaTable (PrimaryKeyFields,
    /// which GoToKey and every Find*Field go through) and ALGoToRecord — must share ONE
    /// resolver. They were separate copies of the same reflection, so fixing either alone
    /// would have left the other refusing on a part.
    /// </summary>
    [Fact]
    public void BothPageIdReaders_GoThroughTheSharedResolver()
    {
        var source = SourceOf("CodeunitPatches.cs");

        // GetMetaTable no longer reads the field itself.
        var getMetaTable = Slice(source, "public static NCLMetaTable NavTestPageBase_GetMetaTable(object self)", 900);
        Assert.Contains("GetPageIdFromTestPage(self)", getMetaTable, StringComparison.Ordinal);
        Assert.DoesNotContain("FindInstanceField(self.GetType(), \"pageUnderTestId\")", getMetaTable,
            StringComparison.Ordinal);

        // ALGoToRecord already did, and still does.
        var alGoToRecord = Slice(source, "public static bool NavTestPageBase_ALGoToRecord(", 900);
        Assert.Contains("GetPageIdFromTestPage(self)", alGoToRecord, StringComparison.Ordinal);

        // And exactly one place reads the raw field: the resolver.
        Assert.Equal(1, Occurrences(source, "FindInstanceField(self.GetType(), \"pageUnderTestId\")"));
    }

    /// <summary>
    /// The resolver's fallback must fire ONLY on 0. A top-level page has a real
    /// pageUnderTestId and must keep answering it — reading the client unconditionally would
    /// make a directly-opened TestPage's id depend on whichever client it got, including the
    /// navigation mock's 0.
    /// </summary>
    [Fact]
    public void TheFallbackIsGuardedOnZero_SoATopLevelPageIsUnaffected()
    {
        var resolver = Slice(SourceOf("CodeunitPatches.cs"),
            "private static int GetPageIdFromTestPage(object self)", 900);

        Assert.Contains("if (pageId != 0) return pageId;", resolver, StringComparison.Ordinal);
        Assert.Contains("ITestPage client", resolver, StringComparison.Ordinal);
        Assert.Contains("client.PageId", resolver, StringComparison.Ordinal);
    }

    // ── Mechanism 2 ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The two entry points must reach the shared scan with OPPOSITE start policies. Asserted
    /// on the delegations rather than on a live search, because the scan needs a NavRecord and
    /// so a loaded BC session — which is what corpus codeunit 60346 measures. What is provable
    /// here is that the distinction exists and points the right way round; collapsing it in
    /// either direction is the defect.
    /// </summary>
    [Fact]
    public void ControlFieldSearchResumesFromTheCursor_TableFieldSearchRestarts()
    {
        // The find-by-value entry points moved to MockTestPage.LivePage.Filters.cs in #3676.
        var source = SourceOf("MockTestPage.LivePage.Filters.cs");

        var byControl = Slice(source,
            "public override bool FindRowFromControlFieldValue(int controlId, object value, bool forward)", 300);
        Assert.Contains("startFromCurrentRow: true", byControl, StringComparison.Ordinal);

        var byTableField = Slice(source,
            "public override bool FindRowFromTableFieldValues(int[] fieldNos, object[] values, bool forward)", 300);
        Assert.Contains("startFromCurrentRow: false", byTableField, StringComparison.Ordinal);

        // FindRowFromControlFieldValue must NOT simply forward to the table-field entry point,
        // which is the pre-#3312 shape and the one that threw the cursor away.
        Assert.DoesNotContain("=> FindRowFromTableFieldValues(new[] { ControlIdToTableFieldNo(controlId) }",
            byControl, StringComparison.Ordinal);
    }

    /// <summary>
    /// The resume branch must be conditional on the record actually standing on a row.
    /// An unpositioned record has no current row, and honouring "start here" then would scan
    /// nothing at all — so FindFirstField, whose BC caller reaches this after MoveFirst(),
    /// would answer false for a value that is on the page.
    /// </summary>
    [Fact]
    public void TheResumeBranchRequiresAnActualCurrentRow()
    {
        var scan = Slice(SourceOf("MockTestPage.LivePage.Filters.cs"),
            "private bool FindRowFromFieldValues(int[] fieldNos, object[] values, bool forward, bool startFromCurrentRow)",
            7000);

        Assert.Contains("startFromCurrentRow && hasCurrent", scan, StringComparison.Ordinal);
        // The restart arm is still the fallback for both "not resuming" and "nowhere to
        // resume from".
        Assert.Contains("forward ? MoveFirst() : MoveLast()", scan, StringComparison.Ordinal);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────

    private static LiveNavTestPart Part(int pageId)
        => new(
            record: null,
            controlIdToFieldNo: new Dictionary<int, int>(),
            creatable: false,
            page: null,
            owner: new object(),
            pageId: pageId,
            parentRecord: null,
            links: []);

    private static string SourceOf(string fileName)
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var path = Path.Combine(repoRoot, "AlRunner", "Patches", fileName);
        Assert.True(File.Exists(path), $"expected to find {path}");
        return File.ReadAllText(path);
    }

    private static string Slice(string source, string anchor, int length)
    {
        var start = source.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(start >= 0, $"could not locate '{anchor}'");
        return source.Substring(start, Math.Min(length, source.Length - start));
    }

    private static int Occurrences(string source, string needle)
    {
        var count = 0;
        for (var i = source.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = source.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
