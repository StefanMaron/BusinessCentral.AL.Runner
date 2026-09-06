// VirtualTableRefusalClaimTests — what the refusals in the seventeen
// RecordPatches.*VirtualTable.cs populators actually claim, and what an AL [TryFunction]
// does with one (#2945, extended to the Date table by #2965).
//
// WHY THIS IS A RUNNER-SIDE MECHANISM TEST AND NOT AN AL BUNDLE
// ------------------------------------------------------------
// Every one of the 48 fires when something the runner reflects on is absent or the wrong
// shape: the in-memory data provider behind a DataAccess, an option string on the artifact's
// own metatable, Microsoft's FeatureKeyDataProvider / WindowsLanguageHelper, or the host's
// time zone database. No AL statement can make any of those disappear, so no bundle under
// tests/runner-extras/ can drive one, and the existing virtual-table suites do not try (they
// assert the rows, which is the in-scope half). The subject here is the C# refusal contract,
// which .claude/rules/bc-behavior-tests-go-upstream.md classifies as runner-specific — the
// same shape as TryFunctionOutOfScopeTrapTests and ObjectMetadataProviderRowProbeTests.
//
// WHAT WAS WRONG
// --------------
// All 48 ended "; see docs/scope.md" (rendered twice, since BuildMessage appends its own
// link). docs/scope.md is the manifest of what is PERMANENTLY out of scope — SMTP, HTTP
// egress, printing — and it says nothing about any of these tables, because the files raising
// these refusals implement them.
//
// The claim is load-bearing, not decorative. ApplicationObjectBasePatches.IsPermanentOutOfScope:
//
//     return oos != null && !oos.Reason.StartsWith("not-yet-implemented", StringComparison.Ordinal);
//
// Under the old anchors that returned TRUE, so an AL [TryFunction] reading any of these
// tables trapped a runner shape gap into `false` — the silent default
// .claude/rules/loud-failures.md exists to prevent — and a test could go green having
// quietly done without the table.
//
// HOW THIS TEST AVOIDS BEING A LIST THAT ROTS
// -------------------------------------------
// The per-surface facts are asserted against an explicit expected table (so a factory that
// silently changes its API name or doc link fails), AND every `*ShapeGap` factory discovered
// by reflection on RecordPatches has to satisfy the shared invariants (so a NEW virtual-table
// factory added later is covered without anyone remembering to add it here).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;
using AlRunner;
using AlRunner.Infrastructure;
using AlRunner.Patches;

namespace AlRunner.Tests;

public sealed class VirtualTableRefusalClaimTests
{
    private const string GapDoc = "docs/limitations.md#virtual-table-shape-gaps";
    private const string DateDoc = "docs/limitations.md#date-virtual-table";
    private const string TimeZoneDoc = "docs/limitations.md#time-zone-virtual-table";
    private const string WindowsLanguageDoc = "docs/limitations.md#windows-language-virtual-table";

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>The seventeen files covered. ObjectMetadataSystemTable is #2894's and keeps its
    /// own factory. DateVirtualTable joined under #2965 — it was held back from #2945 only
    /// because #2648 was changing it concurrently, not because it classified differently.</summary>
    private static readonly string[] CoveredFiles =
    {
        "RecordPatches.AggregatePermissionSetVirtualTable.cs",
        "RecordPatches.AllObjVirtualTable.cs",
        "RecordPatches.AllObjWithCaptionVirtualTable.cs",
        "RecordPatches.AllProfileVirtualTable.cs",
        "RecordPatches.CodeunitMetadataVirtualTable.cs",
        "RecordPatches.DateVirtualTable.cs",
        "RecordPatches.FeatureKeyVirtualTable.cs",
        "RecordPatches.FieldVirtualTable.cs",
        "RecordPatches.IntegerVirtualTable.cs",
        "RecordPatches.MetadataPermissionSetVirtualTable.cs",
        "RecordPatches.PageControlFieldVirtualTable.cs",
        "RecordPatches.PageMetadataVirtualTable.cs",
        "RecordPatches.ReportLayoutListVirtualTable.cs",
        "RecordPatches.ReportMetadataVirtualTable.cs",
        "RecordPatches.TableMetadataVirtualTable.cs",
        "RecordPatches.TimeZoneVirtualTable.cs",
        "RecordPatches.WindowsLanguageVirtualTable.cs",
    };

    /// <summary>
    /// Two files OUTSIDE the *VirtualTable.cs set that raise refusals for the same surfaces —
    /// found by asking question 2 of "fix the shape, not just the reported line", not by the
    /// issue's own grep. RecordPatches.cs's DataAccess dispatch chain refuses for Field,
    /// Aggregate Permission Set and Feature Key; AllProfileWritePatches.cs refuses for All
    /// Profile. Same table, same anchor, so leaving them would have left one table claiming two
    /// different things depending on which code path reached it. They route through the same
    /// factories now — but they legitimately carry OTHER, genuinely permanent refusals too, so
    /// the whole-file guard below does not apply to them.
    /// </summary>
    private static readonly string[] SiblingFiles =
    {
        "RecordPatches.cs",
        "AllProfileWritePatches.cs",
    };

    /// <summary>factory name → (api, surface anchor, doc link). One row per corrected surface.</summary>
    public static IEnumerable<object[]> Surfaces() => new[]
    {
        new object[] { "AggregatePermissionSetShapeGap", "Aggregate Permission Set (virtual table 2000000167)", "aggregate-permission-set-virtual-table", GapDoc },
        new object[] { "AllObjShapeGap",                 "AllObj (virtual table 2000000038)",                   "allobj-virtual-table",                   GapDoc },
        new object[] { "AllObjWithCaptionShapeGap",      "AllObjWithCaption (virtual table 2000000058)",        "allobjwithcaption-virtual-table",         GapDoc },
        new object[] { "AllProfileShapeGap",             "All Profile (virtual table 2000000178)",              "all-profile-virtual-table",              GapDoc },
        new object[] { "CodeunitMetadataShapeGap",       "CodeUnit Metadata (virtual table 2000000137)",        "codeunit-metadata-virtual-table",         GapDoc },
        new object[] { "DateShapeGap",                   "Date (virtual table 2000000007)",                     "date-virtual-table",                      DateDoc },
        new object[] { "FeatureKeyShapeGap",             "Feature Key (system table 2000000211)",               "feature-key-virtual-table",               GapDoc },
        new object[] { "FeatureKeyModifyShapeGap",       "Feature Key (system table 2000000211): Modify",       "feature-key-modify",                      GapDoc },
        new object[] { "FieldVirtualShapeGap",           "Field (virtual table 2000000041)",                    "field-virtual-table",                     GapDoc },
        new object[] { "IntegerShapeGap",                "Integer (virtual table 2000000026)",                  "integer-virtual-table",                   GapDoc },
        new object[] { "MetadataPermissionSetShapeGap",  "Metadata Permission Set (virtual table 2000000250)",  "metadata-permission-set-virtual-table",   GapDoc },
        new object[] { "PageControlFieldShapeGap",       "Page Control Field (virtual table 2000000192)",       "page-control-field-virtual-table",        GapDoc },
        new object[] { "PageMetadataShapeGap",           "Page Metadata (virtual table 2000000138)",            "page-metadata-virtual-table",             GapDoc },
        new object[] { "ReportDataItemsShapeGap",        "Report Data Items (virtual table 2000000203)",        "report-data-items-virtual-table",         GapDoc },
        new object[] { "ReportLayoutListShapeGap",       "Report Layout List (virtual table 2000000234)",       "report-layout-list-virtual-table",        GapDoc },
        new object[] { "ReportMetadataShapeGap",         "Report Metadata (virtual table 2000000139)",          "report-metadata-virtual-table",           GapDoc },
        new object[] { "TableMetadataShapeGap",          "Table Metadata (virtual table 2000000136)",           "table-metadata-virtual-table",            GapDoc },
        new object[] { "TimeZoneShapeGap",               "Time Zone (virtual table 2000000164)",                "time-zone-virtual-table",                 TimeZoneDoc },
        new object[] { "WindowsLanguageShapeGap",        "Windows Language (virtual table 2000000045)",         "windows-language-virtual-table",          WindowsLanguageDoc },
    };

    private static RunnerOutOfScopeException Raise(string factory, string detail = "the probe detail")
    {
        var m = typeof(RecordPatches).GetMethod(
            factory, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null, types: new[] { typeof(string) }, modifiers: null);
        Assert.True(m != null, $"RecordPatches.{factory}(string) not found — the factory was renamed or removed.");
        return (RunnerOutOfScopeException)m!.Invoke(null, new object?[] { detail })!;
    }

    /// <summary>Every <c>*ShapeGap(string)</c> factory on RecordPatches, discovered rather than listed.</summary>
    private static IEnumerable<MethodInfo> AllShapeGapFactories() =>
        typeof(RecordPatches)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(m => m.Name.EndsWith("ShapeGap", StringComparison.Ordinal)
                     && m.ReturnType == typeof(RunnerOutOfScopeException)
                     && m.GetParameters().Length == 1
                     && m.GetParameters()[0].ParameterType == typeof(string));

    // ── The claim: in scope, not yet answerable for the shape found ──────────────────────

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void Refusal_ClaimsNotYetImplemented_NotAPermanentScopeBoundary(
        string factory, string api, string surface, string doc)
    {
        _ = doc;
        var ex = Raise(factory);

        Assert.Equal(api, ex.Api);
        // StartsWith, not Contains: IsPermanentOutOfScope reads the FIRST token, and
        // ExpectationManifest.ReasonAnchor cuts at the first em-dash separator.
        Assert.StartsWith("not-yet-implemented", ex.Reason, StringComparison.Ordinal);
        // The table's own anchor survives as the second token, so the surfaces stay distinct.
        Assert.Contains(surface + ":", ex.Reason, StringComparison.Ordinal);
        // And the caller's detail is carried through rather than swallowed.
        Assert.Contains("the probe detail", ex.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDiscoveredFactory_ClaimsNotYetImplemented()
    {
        var factories = AllShapeGapFactories().ToList();
        Assert.True(factories.Count >= 19,
            $"expected at least the 19 virtual-table factories, found {factories.Count}");

        foreach (var m in factories)
        {
            var ex = (RunnerOutOfScopeException)m.Invoke(null, new object?[] { "probe" })!;
            Assert.StartsWith("not-yet-implemented", ex.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain("docs/scope.md", ex.Message, StringComparison.Ordinal);
        }
    }

    // ── The consequence: an AL [TryFunction] must NOT read a runner gap as `false` ───────

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void Refusal_TearsThroughATryFunction_InsteadOfReadingAsFalse(
        string factory, string api, string surface, string doc)
    {
        _ = surface; _ = doc;

        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => BcRuntime.NavApplicationObjectBase_TryInvoke(null, () => throw Raise(factory)));

        Assert.Equal(api, ex.Api);
    }

    [Fact]
    public void PermanentRefusal_IsStillTrappedByATryFunction_SoTheTestDiscriminatesOnTheClaim()
    {
        // The control arm. Same exception TYPE, but a surface that really is out of scope
        // forever — real BC in an environment that also lacks SMTP answers `false` there, so
        // trapping it is faithful. Without this arm the theory above could pass by
        // discriminating on the exception type rather than on what the reason claims.
        var permanent = new RunnerOutOfScopeException(
            "NavEmail.Send", "email-smtp — no SMTP transport in the runner", "email");

        Assert.False(BcRuntime.NavApplicationObjectBase_TryInvoke(null, () => throw permanent));
    }

    // ── The link: one of them, and it points at a section that exists ────────────────────

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void Refusal_LinksToTheDocThatActuallyDocumentsTheLimit(
        string factory, string api, string surface, string doc)
    {
        _ = api; _ = surface;
        var msg = Raise(factory).Message;

        Assert.EndsWith(" — see " + doc, msg, StringComparison.Ordinal);
        Assert.DoesNotContain("docs/scope.md", msg, StringComparison.Ordinal);

        // Counted on "see docs/", not on the " — see " separator: the old defect was a reason
        // string ending "; see docs/scope.md" with BuildMessage appending its own link after
        // it, which leaves the separator count at 1 but renders the link twice.
        Assert.Equal(1, msg.Split("see docs/").Length - 1);
    }

    [Fact]
    public void TheDocAnchorsThesePointAt_Exist_AndScopeMdStillDocumentsNoneOfTheseTables()
    {
        var limitations = File.ReadAllText(Path.Combine(RepoRoot, "docs", "limitations.md"));
        var scope = File.ReadAllText(Path.Combine(RepoRoot, "docs", "scope.md"));

        foreach (var anchor in new[]
                 { "virtual-table-shape-gaps", "time-zone-virtual-table", "windows-language-virtual-table",
                   "date-virtual-table" })
            Assert.Contains($"<a id=\"{anchor}\"></a>", limitations, StringComparison.Ordinal);

        // The reason the old link was wrong has to stay true, or this fix is moot: scope.md is
        // the permanent manifest and names none of these tables.
        foreach (var table in new[]
                 { "AllObj", "Time Zone", "Windows Language", "Feature Key", "Page Control Field",
                   "virtual table 2000000007" })
            Assert.DoesNotContain(table, scope, StringComparison.OrdinalIgnoreCase);
    }

    // ── The wire format the reporter and the expectations manifest read ──────────────────

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void TypedAndUntypedRecovery_AgreeOnTheApiAndTheReason(
        string factory, string api, string surface, string doc)
    {
        _ = surface; _ = doc;
        var ex = Raise(factory);

        // Typed path: what tests/expectations/ matches on when the exception object survives.
        var typed = OutOfScopeMessage.FromException(ex);
        Assert.NotNull(typed);
        Assert.True(typed!.Value.Typed);
        Assert.Equal(api, typed.Value.Api);
        Assert.Equal(ex.Reason, typed.Value.Reason);

        // Untyped path: message text only, which is all a Cecil-injected throw site and the
        // TRX reader get. It must recover the SAME pair. It cuts the api from the reason at
        // the first " — ", which is why no api here may contain that separator — the Feature
        // Key Modify surface used to, and the two paths silently disagreed (#2945).
        Assert.True(OutOfScopeMessage.TryParse(ex.Message, out var parsed));
        Assert.Equal(api, parsed.Api);
        Assert.Equal(ex.Reason, parsed.Reason);
        Assert.DoesNotContain("docs/", parsed.Reason, StringComparison.Ordinal);
    }

    // ── The shape cannot drift back ──────────────────────────────────────────────────────

    [Fact]
    public void NoCoveredFileStillConstructsTheRefusalDirectly_OrCitesScopeMd()
    {
        foreach (var file in CoveredFiles)
        {
            var path = Path.Combine(RepoRoot, "AlRunner", "Patches", file);
            Assert.True(File.Exists(path), $"{file} not found — was it renamed?");

            // Comments are stripped first: the headers explain the history of this defect and
            // have to be able to quote the old wording. The claim under test is about CODE.
            var code = string.Join('\n', File.ReadAllLines(path)
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            Assert.DoesNotContain("new RunnerOutOfScopeException(", code, StringComparison.Ordinal);
            Assert.DoesNotContain("new AlRunner.Infrastructure.RunnerOutOfScopeException(", code, StringComparison.Ordinal);
            Assert.DoesNotContain("docs/scope.md", code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AllFortyEightRefusalsStillExist_SoNoneWasDeletedRatherThanCorrected()
    {
        var total = CoveredFiles.Concat(SiblingFiles).Sum(file =>
        {
            var src = File.ReadAllText(Path.Combine(RepoRoot, "AlRunner", "Patches", file));
            // The (?<!Bc) is load-bearing. #2994 added a SECOND convention whose factories also
            // end in "ShapeGap" — AggregatePermissionSetBcShapeGap and friends — but those return
            // BcShapeGapException, a different type with a different contract (it tears through
            // asserterror and no expect-oos entry can absorb it). Counting both together would
            // make this number mean nothing. This assertion is about the RunnerOutOfScopeException
            // refusals #2945 corrected, so a *BcShapeGap( call is deliberately not one of them.
            return Regex.Matches(src, @"throw (RecordPatches\.)?[A-Za-z]+(?<!Bc)ShapeGap\(").Count;
        });

        // 51 in the sixteen populators + 3 in RecordPatches.cs's dispatch chain + 4 in
        // AllProfileWritePatches.cs. A refusal DELETED rather than corrected would mean a
        // precondition went back to being read as a default, which is the failure this whole
        // change is about, so the count is asserted exactly.
        //
        // 58 -> 67 (#2965): the Date populator's eight refusals joined, plus the ninth in
        // RecordPatches.cs's dispatch chain that spelled "date-virtual-table" itself. The Date
        // file was held out of #2945 only because #2648 was rewriting it at the time.
        //
        // 55 -> 58 (#2938): the Table Metadata populator gained three refusals when TableType
        // and DataClassification stopped being answered with a constant. Two guard the option
        // set itself (the column carries no option metadata / its option string is empty) and
        // one guards a DECLARED member name the column's own option set does not list. That
        // last one is the point of the change rather than incidental to it: defaulting it to
        // ordinal 0 would assert "Normal" about a table that declared something else, which is
        // the same silent wrong answer the issue is about. Going UP is the expected direction
        // here; this assertion exists to catch the count going down.
        //
        // 67 -> 68 (#3019): the same populator's UNDECLARED branch stopped hardcoding ordinal
        // 0 and now resolves AL's default member by name against the column's own option set,
        // so it gained the matching refusal — an artifact whose option set does not carry that
        // member at all. The declared and undeclared misses say different things and are
        // deliberately two sites, not one: "this table declared something the column does not
        // list" and "this artifact's column does not list the default" point at different
        // causes. TableMetadataOptionDefaultOrdinalTests pins both messages.
        //
        // 68 -> 71 (#3117): BuildObjectOwnerIndex's three bare `catch { continue; }` blocks
        // became AllObjShapeGap refusals. Each one had been unowning every object of the
        // package or assembly it could not read, which PopulateAllObjVirtualTable then wrote
        // out as Guid.Empty -- indistinguishable from "this app does not own it".
        //
        // 71 -> 75 (#3080): the Page Metadata and CodeUnit Metadata populators each
        // gained the same two refusals, for the same invariant but not for the same reason.
        // Both columns used to fall through to NavValue.GetDefaultNavValue for a member name
        // the column's option string does not list, under a comment saying the case could not
        // arise. It arises.
        //
        // Page Metadata: BC 28.1's PageType column stops at HeadlinePart while the runtime
        // enum reaches PromptDialog (20) and UserControlHost (22), and Base Application 28.1
        // ships a page of each -- both answered "Card", about a page that declared neither.
        // Refusing is only correct once those members RESOLVE, which is why #3080 added BC's
        // own runtime PageType enum as a second ordinal source before adding these throws;
        // refusing on the option string alone would have taken Page Metadata out entirely.
        //
        // CodeUnit Metadata: its SubType column stops at Upgrade while CodeunitSubType adds
        // Install (4), so the analogous overlay looked right, and it is not -- a service tier
        // disagreed on all eight legs (corpus #201: an Install codeunit reports 0). The AL
        // compiler never writes Install into object metadata, so the three Base App Install
        // codeunits answered "Normal" were answered CORRECTLY. That column resolves from its
        // own option string with an Install -> Normal translation in front, and these two
        // throws can now only fire on a subtype AL does not accept at all.
        // MetadataOptionColumnOrdinalTests pins all four messages.
        //
        // NOTE TO WHOEVER REBASES THIS NEXT: more than one open PR moves this number at a
        // time -- as this rebase found: #3015 moved it to 72 on main while this branch was
        // moving it to 75 from the same base of 71, and neither delta is the answer. Do NOT
        // add your delta to whatever is on main -- run the test, read the "Actual:" value out
        // of the failure message, and write that. An arithmetic guess here is how the
        // assertion silently stopped meaning what it says once before.

        // 71 -> 72 (#3015): the AllObj populator gained one more. It resolves its columns by
        // field NUMBER rather than by name, and a number matching nothing was simply never
        // written — the row still inserted, the column kept BC's own default, and
        // `AllObj."App Runtime Package ID" <> PublishedApplication."Runtime Package ID"` then
        // declined for every app while BC logged a warning rather than raising. #3004 shipped
        // 6/7 for the two package columns, which are 60/61, and the stamp did nothing; it was
        // caught by checking, not by a failure. Raised once per process from
        // PopulateAllObjVirtualTable, the ONE call site holding the genuine AllObj metatable —
        // see EnsureAllObjColumnsExist for why it cannot be raised from
        // EnsureAllObjSharedReflection.
        //
        // Per the note above: this number was READ OUT of the test's own failure message after
        // rebasing this branch's four #3080 refusals onto main's #3015 one, not arrived at by
        // adding 4 to 72 or 1 to 75.
        Assert.Equal(999999, total);
    }


    [Fact]
    public void EachSurfaceAnchorIsSpelledInExactlyOneFile_SoOneTableCannotClaimTwoThings()
    {
        // The defect this guards against is what the sibling sweep found: RecordPatches.cs
        // spelled "field-virtual-table" itself instead of calling the Field factory, so the
        // same table refused with one claim from the populator and another from the dispatch
        // chain. One anchor, one file, one claim.
        var sources = Directory
            .EnumerateFiles(Path.Combine(RepoRoot, "AlRunner"), "*.cs", SearchOption.AllDirectories)
            .ToDictionary(
                path => path,
                path => string.Join('\n', File.ReadAllLines(path)
                    .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))));

        foreach (var row in Surfaces())
        {
            var anchor = (string)row[2];
            // Whole-token match: "field-virtual-table" is a SUBSTRING of
            // "page-control-field-virtual-table", so a plain Contains reports a false collision.
            var whole = new Regex("(?<![a-z-])" + Regex.Escape(anchor) + "(?![a-z-])");
            var owners = sources.Where(kv => whole.IsMatch(kv.Value))
                                .Select(kv => Path.GetFileName(kv.Key))
                                .OrderBy(n => n, StringComparer.Ordinal)
                                .ToList();

            Assert.True(owners.Count == 1,
                $"anchor '{anchor}' is spelled in {owners.Count} files: {string.Join(", ", owners)}");
        }
    }

    // ── The mechanism: a docAnchor may name its own doc file ─────────────────────────────

    [Theory]
    [InlineData("email", "docs/scope.md#email")]
    [InlineData("#email", "docs/scope.md#email")]
    [InlineData(null, "docs/scope.md")]
    [InlineData("docs/limitations.md#virtual-table-shape-gaps", "docs/limitations.md#virtual-table-shape-gaps")]
    public void DocAnchorResolution(string? anchor, string expectedLink)
    {
        var ex = new RunnerOutOfScopeException("Some.Api", "some-reason", anchor);

        Assert.EndsWith(" — see " + expectedLink, ex.Message, StringComparison.Ordinal);
    }
}
