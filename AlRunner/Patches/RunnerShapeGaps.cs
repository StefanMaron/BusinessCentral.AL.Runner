// RunnerShapeGaps — the refusals raised OUTSIDE the virtual-table populators that are runner
// gaps rather than scope boundaries, and the classification behind each one (#2966).
//
// ── WHAT WAS WRONG ───────────────────────────────────────────────────────────────────────
//   #2894 fixed twelve of these in RecordPatches.ObjectMetadataSystemTable.cs and #2945 fixed
//   55 more across the virtual-table populators. #2966 measured what was left: 77 non-comment
//   "docs/scope.md" citations in 21 files elsewhere under AlRunner/. MOST of them are correct
//   — report rendering, printing, external HTTP and the document service really are permanent
//   — so this file is not a sweep that deletes the citation. It holds the sixteen that were
//   measurably making a false claim.
//
//   docs/scope.md is the manifest of what is PERMANENTLY out of scope. Citing it for a surface
//   the runner intends to support tells the next developer to stop looking, and it has a
//   runtime consequence, which is why this is a correctness fix and not a wording one.
//   ApplicationObjectBasePatches.IsPermanentOutOfScope:
//
//       return oos != null && !oos.Reason.StartsWith("not-yet-implemented", StringComparison.Ordinal);
//
//   Under a docs/scope.md anchor that returns TRUE, so an AL [TryFunction] traps a runner
//   shape gap into `false` — the silent default .claude/rules/loud-failures.md exists to
//   prevent — and the test goes green having quietly done without the surface. With the
//   "not-yet-implemented" anchor the refusal tears through instead.
//
//   The issue named one example and it is real: RecordPatches.QueryProjection.cs's
//   "query-join-synthesized-subquery-not-implemented" says not-implemented in words, but does
//   not START with "not-yet-implemented", so it was swallowed.
//
// ── CLASSIFYING ALL 77 SITES BEFORE TOUCHING ANY ─────────────────────────────────────────
//   The issue counted 96 across 23 files. Re-measured on main after #2894 and #2950 landed,
//   with the same definition (a non-comment line mentioning docs/scope.md), it is 77 across
//   21 files: the twelve Object Metadata sites are already fixed, and two of the issue's
//   files no longer carry one. Every one of the 77 was read before anything was changed.
//
//   Buckets, the same ones #2894 and #2945 used. A refusal belongs in (1) only when BC ITSELF
//   cannot answer, so that an AL [TryFunction] reading `false` is the OBSERVABLE BC OUTCOME
//   rather than a runner gap quietly absorbed.
//
//     (0) not a refusal at all — prose or mechanism mentioning the file .............  6
//     (1) genuinely out of scope — keep the refusal and the scope.md link ...........  28
//     (2) in scope, not yet answerable — needs the "not-yet-implemented" anchor .....  43
//     (3) deliberate divergence — the runner answers on purpose, never throws .......  0
//                                                                                    ----
//                                                                                      77
//
//   The author's estimate that "most are correct" holds for the refusals that cite scope.md
//   as a scope claim: 28 of the 71 real refusals are right and stay exactly as they are. It
//   does not hold as a majority — 43 are gaps wearing a permanence claim.
//
//   NOTHING landed in (3). An expect-divergence surface returns an answer rather than
//   throwing (docs/expectations.md), so by construction it cannot be a refusal site. The one
//   place "docs/scope.md#jobs" appears next to the word divergence is ExpectationManifest.cs,
//   as the EXAMPLE in the error text that tells a manifest author what a Mode=expect-divergence
//   entry's `Doc` field should look like. That is (0), and it is correct as written.
//
//   Of the 43 in (2), EIGHTEEN are corrected here — the ones whose classification is settled
//   and whose file is not being edited concurrently. The other 25 are deferred with a reason
//   and a tracking issue, at the bottom of this header.
//
//   NINE MORE sites are corrected that the issue's measurement could not see, because they
//   are not under AlRunner/ at all: AlRunner.QueryJoin/JoinExecutor.cs, the isolated join
//   executor assembly. It spelled its own reason strings and cited docs/scope.md at all nine.
//   One of them refuses the SAME shape as the site the issue named — a synthesized
//   sub-dataitem that is not the FlowField-calculation shape — reached down the other code
//   path, so that one shape claimed two different things depending on which path found it.
//   Both route through this file's Query factory now. 27 sites corrected in total.
//
// ── (2), CORRECTED HERE: twenty-seven sites in eight files ───────────────────────────────
//
//   RecordPatches.QueryProjection.cs (7), RecordPatches.QueryJoin.cs (2) and
//   AlRunner.QueryJoin/JoinExecutor.cs (9) — eighteen sites.
//     Multi-dataitem joins are IMPLEMENTED and pinned by the upstream corpus
//     (tests/al-language/.../query/TestQueryJoin.al, green on a real service tier and green
//     here). All eighteen are sub-shapes of a working join the executor cannot take yet, or a
//     BC helper that moved: a synthesized sub-dataitem that is not the FlowField-calculation
//     shape, a DataItemLink that is Const/Expression rather than field=field or references a
//     FlowField, a filter keyed by a column outside the projection, NavValue.GetDefaultNavValue
//     or FlowFieldsHelper.NegateValue missing on this BC build. BC answers every one of them.
//
//     The executor could not call this factory before, because JoinContext.OutOfScope was
//     (api, reason) → Exception and the executor wrote the reason itself. It is
//     (api, surface, detail) now, so al-runner composes the anchor in exactly one place for
//     both paths.
//
//     docs/scope.md §3.13 CLAIMED they were permanent — "Multi-dataitem queries (JOINs),
//     aggregations, SaveAsCsv/SaveAsXml/SaveAsJson … a faithful in-memory equivalent is a
//     multi-day workstream". That section had gone stale: the joins landed, and SaveAsJson /
//     SaveAsXml / SaveAsCsv run BC's own implementation against real query metadata. It is
//     corrected in the same change, because leaving it would leave the file these refusals
//     used to cite still agreeing with them. Aggregation is a real gap and stays one (#2137).
//
//   UserTableTriggerPatches.cs (3).
//     The User Property row BC writes alongside every User. Real BC creates it and the runner
//     intends to. Each refusal names a precondition the RUNNER could not meet — no session on
//     the record under insert, no metadata for table 2000000121, a field the metatable does
//     not state. None of the three is BC declining to do the work.
//
//   RunnerModalDispatch.cs (2).
//     FormRunModal / FormRun handed a null test-execution context or a null request. A runner
//     internal invariant, not a surface BC lacks — and precisely the kind of thing a
//     [TryFunction] must never absorb into `false`.
//
//   RecordPatches.InstallBaseline.cs (1).
//     The per-codeunit install-baseline snapshot cannot capture a table backed by something
//     other than TempTableDataProvider. The runner's own snapshot mechanism not covering a
//     case yet; BC has no equivalent concept to decline.
//
//   RunnerTestClientSession.cs (1, GetPage).
//     No form registered under the handle the [ModalPageHandler] is being handed. The runner's
//     own form registry did not have it; BC's client session would.
//
//   NavReportSync.cs (2, SyncRunRequestPage / SyncStaticRun).
//     These two already LED with "not-yet-implemented", so they already tore through a
//     [TryFunction] correctly — but they appended "See docs/scope.md" by hand, which
//     BuildMessage rendered a second time, and sent the reader to a file that documents
//     nothing about report construction. Anchor unchanged, link corrected, doubling removed.
//
// ── (1), LEFT ALONE: twenty-eight sites that really are permanent ────────────────────────
//   Read and kept. Per file, so the next reader does not re-derive them:
//
//     NclCecilRewrite.Reports.cs (6)  RDLC / Word / Excel result-set processors, the print
//                                     server ctor, the document-service decorator ctor.
//                                     External renderers and an external service.
//                                     docs/scope.md#report-rendering, a section that exists.
//     MockTestPage.cs (6)             a page with no SourceTable; an OnQueryClosePage veto,
//                                     which in BC leaves the page open awaiting a user (§3.11);
//                                     a control not bound to a source-table field used to
//                                     locate a row; and three AL-AUTHORING errors real BC also
//                                     raises — an option value that is neither member nor
//                                     caption (twice) and a date spelling SetValue never
//                                     produces. For those three a [TryFunction] reading false
//                                     IS BC's outcome, which is the (1) test.
//     RunnerPageInstance.cs (3)       an action whose effect is RunObject; two lookups that
//                                     come from a TableRelation and would open the related
//                                     table's list page. All §3.11 client interaction.
//     NavReportSync.cs (3)            two layout-rendering throws (#report-rendering) and the
//                                     request-page client-callback fall-through: with no
//                                     [RequestPageHandler] declared, real BC's test framework
//                                     fails the test too.
//     RunnerTestClientSession.cs (2)  CreatePage and ActivatePage — client-window concepts the
//                                     runner's dispatch path never reaches.
//     RequestPageTestPage.cs (2)      a data item or a control the report does not declare;
//                                     both name what IS declared, both are AL-authoring errors.
//     MediaPatches.cs (1)             image decode needs System.Drawing, absent on Linux.
//     RecordPatches.cs (1, l.1632)    table-connections, which IS a scope.md section (§3.15).
//     HelperShims.cs (1)              HttpClient — #external-http.
//     NclCecilRewrite.cs (1) and
//     NclCecilRewrite.Runtime.cs (1)  request-page UI and layout rendering.
//     SkeletonPageCustomizationRepository.cs (1)  page personalization has no store (§3.11).
//
//   Several of those twenty-eight still render the link twice, because they end their reason
//   with "See docs/scope.md" and BuildMessage appends its own. The CLAIM is right in every
//   one, so the doubling belongs to #2766 (still open) and not to this change — half-sweeping
//   it here would leave that issue's measurement wrong without closing it.
//
// ── (0), NOT REFUSALS: six sites ─────────────────────────────────────────────────────────
//   CliText.cs (4) is --guide and --help prose pointing readers at docs/scope.md, and it is
//   right to. RunnerOutOfScopeException.cs (1) is BuildMessage's DefaultDoc constant — the
//   mechanism itself. ExpectationManifest.cs (1) is the example in a validation error.
//
// ── (2), DEFERRED: twenty-five sites, each with a reason ─────────────────────────────────
//   MockTestPage.cs (11) and RunnerPageInstance.cs (5) — sixteen genuine in-scope gaps mixed
//   in with the permanent ones above: "no AL page object was built for this page", "the runner
//   could not resolve this control", "whether field N declares an OnLookup could not be
//   determined on this BC build", "both triggers resolve to member id N". They should tear
//   through. Not corrected here for the reason the issue itself gives — 25 citations across
//   two files that in-flight TestPage work is editing, where a factory sweep would collide.
//   Filed as #2999, which lists all sixteen by line AND lists the fourteen in those same two
//   files that are genuinely permanent, so the follow-up cannot over-sweep them.
//
//   RecordPatches.DateVirtualTable.cs (7) and RecordPatches.cs:1773 (1) — the Date virtual
//   table, including the sibling site that spells "date-virtual-table" itself instead of
//   calling a factory, which is the same defect #2945 fixed for four other tables. #2648 is
//   editing that file concurrently; recorded on #2965 rather than split off.
//
//   MediaPatches.cs (1, the non-seekable content stream) — the runner cannot sniff a header it
//   cannot rewind, which is a mechanism gap rather than a scope boundary. Left with its
//   file-mate above, which IS permanent, so the file is corrected once rather than twice.
//
// ── WHY "not-yet-implemented" AND NOT A NEW EXCEPTION TYPE ───────────────────────────────
//   Whether a BC-shape guard should raise RunnerOutOfScopeException at all is #2946, a
//   convention decision across AlRunner/Patches/ being settled separately. This file uses the
//   anchor as it stands and does not redesign the type.
//
//   No classification moves. ExpectationManifest.ReasonAnchor cuts at the first em-dash, so
//   the anchor these report goes from e.g. "query-join-no-source" to "not-yet-implemented".
//   No manifest entry matches on any of them: the only three Reason values declared today are
//   task-scheduler-create-task, external-http and report-rendering-external.

using AlRunner.Infrastructure;

namespace AlRunner.Patches;

/// <summary>
/// One factory per corrected surface, so a refusal cannot drift back to claiming a permanent
/// scope boundary one call site at a time. See this file's header for the per-site
/// classification, and <see cref="RecordPatches.VirtualTableShapeGap"/> for the same shape
/// applied to the virtual-table populators (#2945).
/// </summary>
internal static class RunnerShapeGap
{
    /// <summary>Where a reader should look for the query-executor gaps. NOT docs/scope.md.</summary>
    internal const string QueryDoc = "docs/limitations.md#query-shape-gaps";

    /// <summary>Where a reader should look for the remaining runtime shape gaps.</summary>
    internal const string RuntimeDoc = "docs/limitations.md#runtime-shape-gaps";

    /// <summary>
    /// The one place these refusals are built. The surface's own anchor is kept as the second
    /// token of the reason so the surfaces stay distinguishable to a reader and to any future
    /// expectations entry, exactly as VirtualTableShapeGap does.
    /// </summary>
    /// <param name="api">The BC surface the AL author touched. Must not contain " — ":
    /// OutOfScopeMessage.TryParse cuts the api from the reason at the first one (#2945).</param>
    /// <param name="surface">The surface's own anchor, e.g. "query-join-no-source".</param>
    /// <param name="detail">What was actually missing.</param>
    /// <param name="docLink">Where the reader should look.</param>
    private static RunnerOutOfScopeException Build(string api, string surface, string detail, string docLink)
        => new(api, $"not-yet-implemented — {surface}: {detail}", docLink);

    /// <summary>A sub-shape of a working multi-dataitem query the executor cannot take yet.</summary>
    internal static RunnerOutOfScopeException Query(string api, string surface, string detail)
        => Build(api, surface, detail, QueryDoc);

    /// <summary>The User Property row BC writes alongside every User could not be written.</summary>
    internal static RunnerOutOfScopeException UserPropertyCompanionRow(string api, string detail)
        => Build(api, "user-property-companion-row", detail, RuntimeDoc);

    /// <summary>The per-codeunit install baseline cannot snapshot this table's backing store.</summary>
    internal static RunnerOutOfScopeException InstallBaselineSnapshot(string api, string detail)
        => Build(api, "install-baseline", detail, RuntimeDoc);

    /// <summary>The runner's own form registry could not hand over the page asked for.</summary>
    internal static RunnerOutOfScopeException ModalPageHandle(string api, string detail)
        => Build(api, "testpage-modal-handle", detail, RuntimeDoc);

    /// <summary>The runner's own modal/page dispatch was handed an incomplete context.</summary>
    internal static RunnerOutOfScopeException ModalDispatchContext(string api, string surface, string detail)
        => Build(api, surface, detail, RuntimeDoc);

    /// <summary>The runner could not construct the report object to run it.</summary>
    internal static RunnerOutOfScopeException ReportConstruction(string api, string detail)
        => Build(api, "report-construction", detail, RuntimeDoc);

    /// <summary>
    /// A column one of the runner's seeded system-table rows is built from is not a field of
    /// that table's metatable, or resolves outside the row (#3015). Company (2000000006),
    /// Published Application (2000000206) and Installed Application (2000000212) are rows a
    /// real service tier writes before any AL runs; the runner writes them from its own state.
    /// A column it cannot write used to be skipped, leaving BC's own default on a row that was
    /// still inserted and still found by its key.
    ///
    /// This is `not-yet-implemented` rather than a `bc-shape-gap:`, deliberately. The read
    /// SUCCEEDED — the metatable was there and answered, it simply states no field of that
    /// name — and BcShapeGapException.cs's own "DO NOT RAISE IT" list names exactly that case
    /// ("a metatable genuinely has no field 3") as an answer about the artifact rather than a
    /// failed read of BC's layout.
    /// </summary>
    internal static RunnerOutOfScopeException SeededSystemTableRow(string api, string detail)
        => Build(api, "seeded-system-table-row", detail, RuntimeDoc);
}
