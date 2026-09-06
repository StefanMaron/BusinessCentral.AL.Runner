// ActionRunPageLinkUnreadableEntryTests — pins how BcAppSymbolCache reads the RunPageLink an
// ACTION of a precompiled dependency page declares, for issue #3267.
//
// What broke, and why "add an `out _`" was the wrong fix
// -----------------------------------------------------
// #3240 taught TryReadActionRunObject to parse an action's RunPageLink with the same parser a
// part's SubPageLink uses (the grammar is identical). Seventeen minutes later #3248 gave that
// parser an `out List<string>? unreadable` and updated the part call site only — its CI had
// run against a base that did not yet contain #3240's call site — so main stopped compiling.
//
// Discarding the new out-parameter at the action call site would have compiled and would have
// been wrong twice over. #3248's whole argument is that a link entry the parser cannot read
// must make the link REFUSE rather than apply its remaining entries, because a link applied
// with one condition missing selects MORE rows than BC's does. That argument does not care
// which property the entries came out of.
//
// It also would have left the real defect in place, which is on the OTHER side of the same
// comparison. The action path's fail-closed channel is DeclaredRunPageLinkEntries vs
// RunPageLink.Count, and #3240 counted the declared entries with a bare SplitTopLevelCommas
// while #3248's parse used the directive-aware SplitPropertyEntries. The AL compiler records a
// property's SOURCE text with its preprocessor directives in it — measured on BC 27.5's Base
// Application part SubPageLinks (#2978), the same grammar — and a comma-split chunk of such
// text can be a directive and nothing else. So the two counters disagree on links that are
// perfectly readable, and the direction of the disagreement is a FALSE REFUSAL: a link the
// parser read in full, refused for being incomplete. Both counters now use one splitter.
//
// These are runner-internal symbol-file parsing claims, not claims about BC
// -------------------------------------------------------------------------
// Nothing here asserts what Business Central does. The subject is how this runner reads
// SymbolReference.json — a Microsoft build artifact — and what it does when a byte sequence in
// it defeats the parser. A BC service tier has no opinion on that: it never reads
// SymbolReference.json at all, because a real tier has the compiled page metadata this whole
// code path exists to substitute for. So these belong in AlRunner.Tests and owe no corpus PR.
//
// The BC-observable behaviour underneath — that an action's RunPageLink filters the page it
// opens — is separately proven upstream and in tests/runner-extras/testpage-promoted-actionref.
using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(CacheRootsSerialCollection.Name)]
public class ActionRunPageLinkUnreadableEntryTests
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

    // Ids distinct from every other fixture in this suite: BcAppSymbolCache's on-disk cache and
    // RecordPatches' dependency state are both process-global, so a shared id risks reading
    // back another test's answer.
    private const int PageId = 88231701;
    private const int PlainActionId = 640647001;
    private const int DirectiveActionId = 640647002;
    private const int UnreadableActionId = 640647003;
    private const int NoLinkActionId = 640647004;

    // Every RunPageLink below is verbatim-plausible AL property text as the compiler records
    // it. The directive one is the BC 27.5 Base Application shape #2978 measured on parts
    // (page 76 "Resource Card" / page 77 "Resource List"), moved onto an action: the trailing
    // "#endif" line is what a bare comma split turns into a third "declared" entry.
    private const string SymbolReference = """
        {
          "RuntimeVersion": "17.0",
          "Pages": [
            {
              "Id": 88231701,
              "Name": "ARPL Dep Page",
              "Properties": [ { "Name": "PageType", "Value": "List" } ],
              "Actions": [
                {
                  "Id": 2, "Name": "processing",
                  "Actions": [
                    { "Kind": 2, "Id": 640647001, "Name": "Plain Link",
                      "Properties": [
                        { "Name": "RunObject", "Value": "ARPL Target" },
                        { "Name": "RunPageLink", "Value": "\"Document No.\" = field(\"No.\"), \"Line No.\" = const(0)" }
                      ] },
                    { "Kind": 2, "Id": 640647002, "Name": "Directive Link",
                      "Properties": [
                        { "Name": "RunObject", "Value": "ARPL Target" },
                        { "Name": "RunPageLink", "Value": "\"Document No.\" = field(\"No.\"),\n#if not CLEAN25\n\"Zone Filter\" = field(\"Zone Filter\"),\n#endif\n" }
                      ] },
                    { "Kind": 2, "Id": 640647003, "Name": "Unreadable Link",
                      "Properties": [
                        { "Name": "RunObject", "Value": "ARPL Target" },
                        { "Name": "RunPageLink", "Value": "\"Document No.\" = field(\"No.\"), \"Amount\" whenever(42)" }
                      ] },
                    { "Kind": 2, "Id": 640647004, "Name": "No Link",
                      "Properties": [
                        { "Name": "RunObject", "Value": "ARPL Target" }
                      ] }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private static void WithRunObjects(Action<Dictionary<int, BcAppSymbolCache.ActionRunObjectSymbol>> body)
    {
        var dir = TestScratch.Dir("al-runner-action-runpagelink-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);
            var page = Assert.Single(BcAppSymbolCache.Get(appPath).Pages, p => p.Id == PageId);
            body(page.MemberIdToRunObject!);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// The regression #3267 is really about. `"Document No." = field("No."),` + `#if not
    /// CLEAN25` + `"Zone Filter" = field("Zone Filter"),` + `#endif` comma-splits into THREE
    /// chunks, the third of which holds only the `#endif` line. Counting declared entries with
    /// a bare SplitTopLevelCommas made that 3, the directive-aware parse correctly produced 2,
    /// and LinksFromSymbols refuses whenever those two differ — so this link, which the parser
    /// read completely and correctly, was refused for being incomplete.
    ///
    /// <para>Both entries must also be PRESENT and correct, not merely counted: a fix that made
    /// the numbers agree by dropping the guarded entry would satisfy a count assertion while
    /// re-introducing exactly the under-filtering #2978 removed.</para>
    /// </summary>
    [Fact]
    public void DirectiveGuardedRunPageLink_CountsDeclaredEntriesTheWayItParsesThem_SoAReadableLinkIsNotRefused()
        => WithRunObjects(runObjects =>
        {
            var spec = runObjects[DirectiveActionId];

            // 2, not 3. The "#endif"-only chunk is not a declared entry.
            Assert.Equal(2, spec.DeclaredRunPageLinkEntries);
            Assert.Equal(2, spec.RunPageLink!.Count);
            // The equality LinksFromSymbols refuses on — stated directly, because it is the
            // thing that was false.
            Assert.Equal(spec.DeclaredRunPageLinkEntries, spec.RunPageLink.Count);
            Assert.Null(spec.UnreadableRunPageLinkEntries);

            Assert.Equal("Document No.", spec.RunPageLink[0].PartFieldName);
            Assert.Equal("field", spec.RunPageLink[0].Kind);
            Assert.Equal("\"No.\"", spec.RunPageLink[0].Value);
            Assert.False(spec.RunPageLink[0].Conditional);

            // The directive-guarded entry is kept AND marked conditional — it may not be in the
            // compiled app, and the consumer resolves that against the app's field inventory
            // rather than guessing (#2978).
            Assert.Equal("Zone Filter", spec.RunPageLink[1].PartFieldName);
            Assert.Equal("field", spec.RunPageLink[1].Kind);
            Assert.Equal("\"Zone Filter\"", spec.RunPageLink[1].Value);
            Assert.True(spec.RunPageLink[1].Conditional,
                "an entry inside #if not CLEAN25 is conditional; treating it as unconditional " +
                "would refuse the whole page when the guarded field is absent");
        });

    /// <summary>
    /// The half `out _` would have thrown away. `"Amount" whenever(42)` is not AL the parser
    /// has ever seen — the three kinds the entry regex accepts (field/const/filter) are the
    /// only ones AL defines — so the link cannot be applied. It must be carried, by TEXT, and
    /// the entry count must still record that two entries were declared, because that
    /// difference is what makes the consumer refuse instead of applying the readable half.
    /// </summary>
    [Fact]
    public void UnreadableRunPageLinkEntry_IsCarriedByTextAndStillCountedAsDeclared()
        => WithRunObjects(runObjects =>
        {
            var spec = runObjects[UnreadableActionId];

            Assert.Equal(2, spec.DeclaredRunPageLinkEntries);
            Assert.Equal(1, spec.RunPageLink!.Count);
            Assert.NotEqual(spec.DeclaredRunPageLinkEntries, spec.RunPageLink.Count);

            var unreadable = Assert.Single(spec.UnreadableRunPageLinkEntries!);
            Assert.Equal("\"Amount\" whenever(42)", unreadable);

            // The readable entry is read correctly — the point is that it is NOT applied alone,
            // not that it failed to parse.
            Assert.Equal("Document No.", spec.RunPageLink[0].PartFieldName);
            Assert.Equal("field", spec.RunPageLink[0].Kind);
        });

    /// <summary>
    /// Negative direction, both arms, so neither assertion above can be satisfied by a
    /// constant. A plain two-entry link declares 2, parses 2 and carries no unreadable text;
    /// an action with a RunObject and no RunPageLink at all declares 0 and reports
    /// HasRunPageLink false, which is what stops the consumer refusing an action that simply
    /// has no link to apply.
    /// </summary>
    [Fact]
    public void PlainRunPageLink_CarriesNoUnreadableEntries_AndAnActionWithNoLinkDeclaresNone()
        => WithRunObjects(runObjects =>
        {
            var plain = runObjects[PlainActionId];
            Assert.Equal(2, plain.DeclaredRunPageLinkEntries);
            Assert.Equal(2, plain.RunPageLink!.Count);
            Assert.Null(plain.UnreadableRunPageLinkEntries);
            Assert.True(plain.HasRunPageLink);
            Assert.Equal("const", plain.RunPageLink[1].Kind);
            Assert.Equal("0", plain.RunPageLink[1].Value);
            Assert.False(plain.RunPageLink[0].Conditional,
                "no directive is present, so no entry may be marked conditional");

            var noLink = runObjects[NoLinkActionId];
            Assert.Equal("ARPL Target", noLink.ObjectName);
            Assert.Equal(0, noLink.DeclaredRunPageLinkEntries);
            Assert.Null(noLink.RunPageLink);
            Assert.Null(noLink.UnreadableRunPageLinkEntries);
            Assert.False(noLink.HasRunPageLink);
        });
}
