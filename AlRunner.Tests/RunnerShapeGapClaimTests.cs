// RunnerShapeGapClaimTests — what the eighteen corrected refusals outside the virtual-table
// populators actually claim, and what an AL [TryFunction] does with one (#2966).
//
// WHY THIS IS A RUNNER-SIDE MECHANISM TEST AND NOT AN AL BUNDLE
// ------------------------------------------------------------
// Each of the eighteen fires when something the RUNNER owns is absent or the wrong shape: a
// join sub-shape its executor does not take, the skeleton session behind a User record, the
// backing provider of a table in the install baseline, its own form registry, a report object
// it could not construct, a null dispatch context. No AL statement can arrange any of those,
// so no bundle under tests/runner-extras/ can drive one — the subject is the C# refusal
// contract, which .claude/rules/bc-behavior-tests-go-upstream.md classifies as runner-specific
// (the same shape as VirtualTableRefusalClaimTests and TryFunctionOutOfScopeTrapTests).
//
// WHAT WAS WRONG
// --------------
// All eighteen cited docs/scope.md, the manifest of what is PERMANENTLY out of scope. The
// claim is load-bearing, not decorative — ApplicationObjectBasePatches.IsPermanentOutOfScope:
//
//     return oos != null && !oos.Reason.StartsWith("not-yet-implemented", StringComparison.Ordinal);
//
// so under a scope.md anchor an AL [TryFunction] trapped a runner gap into `false`, and the
// test went green having quietly done without the surface. The nine query sites are the case
// the issue named: "query-join-synthesized-subquery-not-implemented" says not-implemented in
// words yet does not START with the token the trap reads.
//
// THE CONTROL ARMS
// ----------------
// Two of them, and they are what stops this suite from passing by discriminating on the
// exception TYPE rather than on what the reason claims:
//   * PermanentRefusal_IsStillTrappedByATryFunction — same type, a genuinely permanent
//     surface, still absorbed into `false`, because that is what real BC in an environment
//     lacking the surface does.
//   * KeptRefusals_StillCiteScopeMd — the twenty-eight sites classified (1) were read and
//     LEFT ALONE. If a later sweep deletes their citation the classification was wrong, and
//     this fails rather than silently widening the change.

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

public sealed class RunnerShapeGapClaimTests
{
    private const string QueryDoc = "docs/limitations.md#query-shape-gaps";
    private const string RuntimeDoc = "docs/limitations.md#runtime-shape-gaps";

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>
    /// The eighteen corrected sites, one entry per distinct surface, keyed by a name
    /// <see cref="MemberData"/> can carry. The builder reproduces the arguments the real call
    /// site passes, so a factory whose api or anchor is quietly changed fails here.
    /// </summary>
    private static readonly Dictionary<string, (Func<RunnerOutOfScopeException> Build, string Api, string Surface, string Doc)> Sites =
        new()
        {
            ["query-join-synthesized-subquery"] = (
                () => Invoke("Query", "NavQuery (multi-dataitem join with a synthesized sub-dataitem)", "query-join-synthesized-subquery", "the probe detail"),
                "NavQuery (multi-dataitem join with a synthesized sub-dataitem)", "query-join-synthesized-subquery", QueryDoc),
            ["query-having-filter-on-nonprojected-column"] = (
                () => Invoke("Query", "NavQuery.SetRange/SetFilter on an aggregated column", "query-having-filter-on-nonprojected-column", "the probe detail"),
                "NavQuery.SetRange/SetFilter on an aggregated column", "query-having-filter-on-nonprojected-column", QueryDoc),
            ["query-join-runtime-filter-on-nonprojected-column"] = (
                () => Invoke("Query", "NavQuery (multi-dataitem join)", "query-join-runtime-filter-on-nonprojected-column", "the probe detail"),
                "NavQuery (multi-dataitem join)", "query-join-runtime-filter-on-nonprojected-column", QueryDoc),
            ["query-join-runtime-filter-unresolved-column"] = (
                () => Invoke("Query", "NavQuery (multi-dataitem join)", "query-join-runtime-filter-unresolved-column", "the probe detail"),
                "NavQuery (multi-dataitem join)", "query-join-runtime-filter-unresolved-column", QueryDoc),
            ["query-join-static-columnfilter-unresolved-column"] = (
                () => Invoke("Query", "NavQuery (multi-dataitem join)", "query-join-static-columnfilter-unresolved-column", "the probe detail"),
                "NavQuery (multi-dataitem join)", "query-join-static-columnfilter-unresolved-column", QueryDoc),
            ["query-join-leftouter-default"] = (
                () => Invoke("Query", "NavQuery (multi-dataitem join)", "query-join-leftouter-default", "the probe detail"),
                "NavQuery (multi-dataitem join)", "query-join-leftouter-default", QueryDoc),
            ["query-join-no-source"] = (
                () => Invoke("Query", "NavQuery (multi-dataitem join)", "query-join-no-source", "the probe detail"),
                "NavQuery (multi-dataitem join)", "query-join-no-source", QueryDoc),
            ["query-reversesign-negatevalue-missing"] = (
                () => Invoke("Query", "Query column ReverseSign", "query-reversesign-negatevalue-missing", "the probe detail"),
                "Query column ReverseSign", "query-reversesign-negatevalue-missing", QueryDoc),

            ["user-property-companion-row"] = (
                () => Invoke("UserPropertyCompanionRow", "User (2000000120) insert", "the probe detail"),
                "User (2000000120) insert", "user-property-companion-row", RuntimeDoc),
            ["install-baseline"] = (
                () => Invoke("InstallBaselineSnapshot", "install-baseline snapshot (table 27)", "the probe detail"),
                "install-baseline snapshot (table 27)", "install-baseline", RuntimeDoc),
            ["testpage-modal-handle"] = (
                () => Invoke("ModalPageHandle", "TestPage modal page (handle 0000)", "the probe detail"),
                "TestPage modal page (handle 0000)", "testpage-modal-handle", RuntimeDoc),
            ["testpage-modal-dispatch-context"] = (
                () => Invoke("ModalDispatchContext", "TestPage modal dispatch", "testpage-modal-dispatch-context", "the probe detail"),
                "TestPage modal dispatch", "testpage-modal-dispatch-context", RuntimeDoc),
            ["testpage-page-dispatch-context"] = (
                () => Invoke("ModalDispatchContext", "TestPage page dispatch", "testpage-page-dispatch-context", "the probe detail"),
                "TestPage page dispatch", "testpage-page-dispatch-context", RuntimeDoc),
            ["report-construction"] = (
                () => Invoke("ReportConstruction", "NavReport.RunRequestPage(50100)", "the probe detail"),
                "NavReport.RunRequestPage(50100)", "report-construction", RuntimeDoc),
        };

    public static IEnumerable<object[]> SiteNames() => Sites.Keys.Select(k => new object[] { k });

    private static readonly Type ShapeGapType =
        typeof(RecordPatches).Assembly.GetType("AlRunner.Patches.RunnerShapeGap")
        ?? throw new InvalidOperationException("AlRunner.Patches.RunnerShapeGap not found — was the factory renamed?");

    private static RunnerOutOfScopeException Invoke(string factory, params string[] args)
    {
        var m = ShapeGapType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .SingleOrDefault(x => x.Name == factory && x.GetParameters().Length == args.Length
                                  && x.GetParameters().All(p => p.ParameterType == typeof(string)));
        Assert.True(m != null, $"RunnerShapeGap.{factory}({args.Length} strings) not found — renamed or removed.");
        return (RunnerOutOfScopeException)m!.Invoke(null, args.Cast<object?>().ToArray())!;
    }

    /// <summary>Files whose docs/scope.md refusals were ALL corrected: none may remain.</summary>
    private static readonly string[] FullyCorrectedFiles =
    {
        "RecordPatches.QueryProjection.cs",
        "RecordPatches.QueryJoin.cs",
        "UserTableTriggerPatches.cs",
        "RecordPatches.InstallBaseline.cs",
        "RunnerModalDispatch.cs",
    };

    /// <summary>
    /// Files that carry BOTH a corrected gap and a genuinely permanent refusal. The residual
    /// count is asserted exactly, so neither an over-sweep (deleting a true scope claim) nor
    /// an under-sweep (leaving a gap citing scope.md) can pass.
    /// </summary>
    private static readonly (string File, int KeptScopeMdSites)[] PartiallyCorrectedFiles =
    {
        ("RunnerTestClientSession.cs", 2),   // CreatePage + ActivatePage: client-window concepts
        ("NavReportSync.cs", 3),             // 2 layout-rendering throws + the client-callback fall-through
    };

    // ── The claim: in scope, not yet answerable for the shape found ──────────────────────

    [Theory]
    [MemberData(nameof(SiteNames))]
    public void Refusal_ClaimsNotYetImplemented_NotAPermanentScopeBoundary(string site)
    {
        var (build, api, surface, _) = Sites[site];
        var ex = build();

        Assert.Equal(api, ex.Api);
        // StartsWith, not Contains: IsPermanentOutOfScope reads the FIRST token, and
        // ExpectationManifest.ReasonAnchor cuts at the first em-dash separator.
        Assert.StartsWith("not-yet-implemented", ex.Reason, StringComparison.Ordinal);
        // The surface's own anchor survives as the second token, so the surfaces stay distinct.
        Assert.Contains(surface + ":", ex.Reason, StringComparison.Ordinal);
        // And the caller's detail is carried through rather than swallowed.
        Assert.Contains("the probe detail", ex.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryFactoryOnRunnerShapeGap_ClaimsNotYetImplemented_AndNeverCitesScopeMd()
    {
        // Discovered, not listed: a factory added later is held to the same invariants without
        // anyone remembering to extend the table above.
        var factories = ShapeGapType
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(m => m.ReturnType == typeof(RunnerOutOfScopeException)
                        && m.GetParameters().All(p => p.ParameterType == typeof(string))
                        // Build is the private plumbing every factory delegates to; it takes
                        // the doc link as an argument, so probing it proves nothing about a
                        // call site. Every PUBLIC-facing factory pins its own doc link.
                        && m.Name != "Build")
            .ToList();

        Assert.True(factories.Count >= 6, $"expected at least the six factories, found {factories.Count}");

        foreach (var m in factories)
        {
            var args = m.GetParameters().Select((_, i) => (object?)$"probe{i}").ToArray();
            var ex = (RunnerOutOfScopeException)m.Invoke(null, args)!;
            Assert.StartsWith("not-yet-implemented", ex.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain("docs/scope.md", ex.Message, StringComparison.Ordinal);
        }
    }

    // ── The consequence: an AL [TryFunction] must NOT read a runner gap as `false` ───────

    [Theory]
    [MemberData(nameof(SiteNames))]
    public void Refusal_TearsThroughATryFunction_InsteadOfReadingAsFalse(string site)
    {
        var (build, api, _, _) = Sites[site];

        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => BcRuntime.NavApplicationObjectBase_TryInvoke(null, () => throw build()));

        Assert.Equal(api, ex.Api);
    }

    [Fact]
    public void PermanentRefusal_IsStillTrappedByATryFunction_SoTheTestDiscriminatesOnTheClaim()
    {
        // Control arm one. Same exception TYPE, but a surface that really is out of scope
        // forever — and one of THIS issue's own (1) bucket, not a borrowed example: report
        // rendering needs an external renderer, so real BC without one answers `false` too.
        var permanent = new RunnerOutOfScopeException(
            "ReportResultSetProcessorFactory.GetRdlcResultSetProcessor",
            "report-rendering-external — RDLC layout processing requires an external renderer",
            "report-rendering");

        Assert.False(BcRuntime.NavApplicationObjectBase_TryInvoke(null, () => throw permanent));

        // And the same for the email surface scope.md leads with, so the arm does not rest on
        // one anchor spelling.
        var email = new RunnerOutOfScopeException("NavEmail.Send", "email-smtp — no SMTP transport", "email");
        Assert.False(BcRuntime.NavApplicationObjectBase_TryInvoke(null, () => throw email));
    }

    // ── The link: one of them, and it points at a section that exists ────────────────────

    [Theory]
    [MemberData(nameof(SiteNames))]
    public void Refusal_LinksToTheDocThatActuallyDocumentsTheLimit(string site)
    {
        var (build, _, _, doc) = Sites[site];
        var msg = build().Message;

        Assert.EndsWith(" — see " + doc, msg, StringComparison.Ordinal);
        Assert.DoesNotContain("docs/scope.md", msg, StringComparison.Ordinal);

        // Counted on "see docs/", not on the " — see " separator: the old defect was a reason
        // string ending "see docs/scope.md" with BuildMessage appending its own link after it,
        // which leaves the separator count at 1 but renders the link twice.
        Assert.Equal(1, msg.Split("see docs/").Length - 1);
    }

    [Fact]
    public void TheDocAnchorsThesePointAt_Exist()
    {
        var limitations = File.ReadAllText(Path.Combine(RepoRoot, "docs", "limitations.md"));

        foreach (var anchor in new[] { "query-shape-gaps", "runtime-shape-gaps" })
            Assert.Contains($"<a id=\"{anchor}\"></a>", limitations, StringComparison.Ordinal);
    }

    [Fact]
    public void ScopeMd_NoLongerClaimsMultiDataItemQueriesArePermanentlyOutOfScope()
    {
        // The reason the query citations were wrong has to stay true, or the fix is moot:
        // scope.md §3.13 asserted multi-dataitem joins and the SaveAs* family were permanent,
        // while the corpus pins the joins as working on a real service tier.
        var scope = File.ReadAllText(Path.Combine(RepoRoot, "docs", "scope.md"));

        Assert.DoesNotContain(
            "| Multi-dataitem queries (JOINs), aggregations", scope, StringComparison.Ordinal);
        Assert.Contains(
            "Nothing about NavQuery is permanently out of scope", scope, StringComparison.Ordinal);
        // The anchor stays, because refusals used to point at it and readers still arrive here.
        Assert.Contains("<a id=\"navquery\"></a>", scope, StringComparison.Ordinal);
    }

    // ── The wire format the reporter and the expectations manifest read ──────────────────

    [Theory]
    [MemberData(nameof(SiteNames))]
    public void TypedAndUntypedRecovery_AgreeOnTheApiAndTheReason(string site)
    {
        var (build, api, _, _) = Sites[site];
        var ex = build();

        var typed = OutOfScopeMessage.FromException(ex);
        Assert.NotNull(typed);
        Assert.True(typed!.Value.Typed);
        Assert.Equal(api, typed.Value.Api);
        Assert.Equal(ex.Reason, typed.Value.Reason);

        // Untyped path: message text only, which is all a Cecil-injected throw site and the TRX
        // reader get. It cuts the api from the reason at the first " — ", so no api here may
        // contain that separator (#2945's Feature Key Modify defect).
        Assert.True(OutOfScopeMessage.TryParse(ex.Message, out var parsed));
        Assert.Equal(api, parsed.Api);
        Assert.Equal(ex.Reason, parsed.Reason);
        Assert.DoesNotContain("docs/", parsed.Reason, StringComparison.Ordinal);
    }

    // ── The shape cannot drift back ──────────────────────────────────────────────────────

    [Fact]
    public void CorrectedFiles_NoLongerConstructTheRefusalDirectly_OrCiteScopeMd()
    {
        foreach (var file in FullyCorrectedFiles)
        {
            var code = CodeOf(file);
            Assert.DoesNotContain("docs/scope.md", code, StringComparison.Ordinal);
            Assert.DoesNotContain("new AlRunner.Infrastructure.RunnerOutOfScopeException(", code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void KeptRefusals_StillCiteScopeMd_SoTheClassificationWasNotQuietlyWidened()
    {
        // Control arm two. The twenty-eight sites classified "genuinely out of scope" were read
        // and deliberately left alone. A later sweep that deletes their citation would be
        // asserting something this change measured and rejected.
        foreach (var (file, kept) in PartiallyCorrectedFiles)
        {
            var actual = Regex.Matches(CodeOf(file), "docs/scope\\.md").Count;
            Assert.True(actual == kept,
                $"{file}: expected {kept} kept docs/scope.md refusal(s), found {actual}");
        }
    }

    [Fact]
    public void AllEighteenCorrectedRefusalsUnderAlRunnerStillExist_SoNoneWasDeletedRatherThanCorrected()
    {
        var files = FullyCorrectedFiles.Concat(PartiallyCorrectedFiles.Select(x => x.File));
        var total = files.Sum(f =>
            Regex.Matches(File.ReadAllText(PathOf(f)), @"throw (AlRunner\.Patches\.)?RunnerShapeGap\.").Count);

        // A refusal DELETED rather than corrected means a precondition went back to being read
        // as a default, which is the failure this whole change is about. Asserted exactly.
        Assert.Equal(18, total);
    }

    // ── The nine sites the issue's own measurement could not see ─────────────────────────

    [Fact]
    public void TheIsolatedJoinExecutor_RoutesItsNineRefusalsThroughTheSameFactory()
    {
        // AlRunner.QueryJoin/JoinExecutor.cs is a separate assembly, so it is outside the
        // AlRunner/ tree the issue measured. It spelled its own reason strings and cited
        // docs/scope.md at all nine — including "query-join-synthesized-subquery", the SAME
        // shape the in-proc mirror in RecordPatches.QueryProjection.cs refuses, reached down
        // the other path. One shape, two claims, depending on which path found it.
        var src = File.ReadAllText(Path.Combine(RepoRoot, "AlRunner.QueryJoin", "JoinExecutor.cs"));
        var code = string.Join('\n', src.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.DoesNotContain("docs/scope.md", code, StringComparison.Ordinal);
        Assert.Equal(9, Regex.Matches(code, @"ctx\.OutOfScope\(").Count);
    }

    [Fact]
    public void TheJoinContextAdapter_ComposesTheAnchor_SoTheExecutorCannotSpellItsOwn()
    {
        // The delegate was (api, reason) → Exception, which is what let the executor write a
        // reason that a [TryFunction] absorbed. It is (api, surface, detail) now, and this
        // pins BOTH halves: the adapter's arity, and what it builds.
        var adapter = typeof(RecordPatches).GetMethod(
            "Join_OutOfScope", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.True(adapter != null, "RecordPatches.Join_OutOfScope not found — renamed or removed.");
        Assert.Equal(3, adapter!.GetParameters().Length);

        var ex = (RunnerOutOfScopeException)adapter.Invoke(
            null, new object?[] { "NavQuery (multi-dataitem join)", "query-join-no-link", "the probe detail" })!;

        Assert.Equal("NavQuery (multi-dataitem join)", ex.Api);
        Assert.StartsWith("not-yet-implemented", ex.Reason, StringComparison.Ordinal);
        Assert.Contains("query-join-no-link:", ex.Reason, StringComparison.Ordinal);
        Assert.EndsWith(" — see " + QueryDoc, ex.Message, StringComparison.Ordinal);

        // The consequence, on the executor's own path this time.
        var thrown = Assert.Throws<RunnerOutOfScopeException>(
            () => BcRuntime.NavApplicationObjectBase_TryInvoke(null, () => throw ex));
        Assert.Equal(ex.Api, thrown.Api);
    }

    private static string PathOf(string file) => Path.Combine(RepoRoot, "AlRunner", "Patches", file);

    /// <summary>File contents with comment lines stripped — the headers quote the OLD wording
    /// on purpose, and the claim under test is about code.</summary>
    private static string CodeOf(string file)
    {
        var path = PathOf(file);
        Assert.True(File.Exists(path), $"{file} not found — was it renamed?");
        return string.Join('\n', File.ReadAllLines(path)
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }
}
