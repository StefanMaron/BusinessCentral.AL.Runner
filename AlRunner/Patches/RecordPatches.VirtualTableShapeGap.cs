// RecordPatches.VirtualTableShapeGap — the one place a virtual-table populator in this
// directory builds its refusal, and the per-surface classification behind it (#2945).
//
// ── WHAT WAS WRONG ───────────────────────────────────────────────────────────────────────
//   Every RecordPatches.*VirtualTable.cs populator refused the same way:
//
//       ?? throw new RunnerOutOfScopeException(
//              "AllObj (virtual table 2000000038)",
//              "allobj-virtual-table — AllObj data access has no in-memory provider; "
//              + "see docs/scope.md");
//
//   docs/scope.md is the manifest of what is PERMANENTLY out of scope — SMTP, HTTP egress,
//   printing, a real task scheduler. Not one of these tables is in it, and the files raising
//   these refusals IMPLEMENT the tables: .claude/rules/loud-failures.md puts AL records
//   squarely in scope. So the citation told the next developer the surface would never work
//   and to stop looking, and it did it twice over — the reason string ended "see
//   docs/scope.md" and RunnerOutOfScopeException.BuildMessage appended its own default link
//   after it.
//
//   The claim also had a runtime consequence, which is why this is a correctness fix and not
//   a wording one. ApplicationObjectBasePatches.IsPermanentOutOfScope:
//
//       return oos != null && !oos.Reason.StartsWith("not-yet-implemented", StringComparison.Ordinal);
//
//   Under the old anchors that returned TRUE, so an AL [TryFunction] reading any of these
//   tables trapped a runner shape gap into `false` — the silent default loud-failures.md
//   exists to prevent. A test could go green having quietly done without the table. With the
//   "not-yet-implemented" anchor the refusal tears through instead.
//
// ── CLASSIFYING THE 48 SITES ─────────────────────────────────────────────────────────────
//   Every site was read and put in one of three buckets before it was touched:
//
//     (1) genuinely out of scope        — keep the refusal, correct the reason.  0 sites.
//     (2) in scope, not yet answerable  — "not-yet-implemented" anchor.         48 sites.
//     (3) implementable now             — say what it would take.                0 sites.
//
//   Nothing landed in (1). To be in (1) a refusal has to be faithful to real BC: BC itself
//   must be unable to answer, so an AL [TryFunction] reading `false` is the OBSERVABLE BC
//   OUTCOME rather than a runner gap. Every one of these tables answers on a real service
//   tier, so a refusal here is always the runner failing to keep up, never BC's answer.
//
//   Nothing landed in (3) either, and that is worth stating precisely rather than assuming.
//   These are not unbuilt features waiting for someone to build them; they are preconditions
//   that hold in every supported configuration. Three shapes, all of them:
//
//     a. "data access has no in-memory provider" (16 sites, one per populator plus Field's
//        row-builder bind). The runner's own store wiring did not hand a provider over.
//        There is nothing to populate, and standing up a private store here would answer
//        with rows nobody can read back.
//     b. "BC's metatable / option string / helper type is not the shape this drives"
//        (23 sites). The artifact's own metadata is the row set or the ordinal source. A
//        hardcoded fallback would be an invented answer, and for an option ordinal it is
//        worse than that: the ordinal is a stored column value, so a wrong guess mis-keys
//        every row silently. Naming the member that moved is the whole value of the refusal.
//     c. "BC's own provider threw, or produced nothing" (9 sites). Feature Key, Field, Time
//        Zone and Windows Language all delegate to Microsoft's provider or to the host, and
//        each of these guards already carries a comment explaining why answering empty would
//        be a wrong answer rather than a missing one (an empty Feature Key table silently
//        wins every legacy code path; an empty Time Zone table is the bug #2584 fixed).
//
//   Two surfaces have a doc section of their own and point at it — Time Zone and Windows
//   Language both document a real, permanent DIVERGENCE (host-derived ids; chosen license
//   columns) that is separate from these shape gaps. Everything else points at
//   docs/limitations.md#virtual-table-shape-gaps.
//
//   What the "not-yet-implemented" anchor tracks is #2946: the runner has no exception type
//   that says "I could not read BC's internals here", and the conventions in this directory
//   disagree about which one to raise for exactly that. That issue stays open.
//
// ── DELIBERATELY NOT COVERED HERE ────────────────────────────────────────────────────────
//   RecordPatches.ObjectMetadataSystemTable.cs got the same treatment under #2894 and keeps
//   its own ObjectMetadataShapeGap factory.
//
//   RecordPatches.DateVirtualTable.cs is left alone: it is being changed concurrently under
//   #2648, so a factory edit across its eight refusals would have collided with that work.
//   Tracked as #2965 rather than swept here, and one of its eight (the row-cap refusal, which
//   tests/runner-extras/date-virtual-table-window pins) may not classify the same way as the
//   other seven — that issue says so instead of assuming.
//
//   96 further "see docs/scope.md" citations sit in 23 files outside this directory. MOST are
//   correct — SMTP, HTTP egress, file storage and printing really are permanent — but at least
//   one is the same defect (RecordPatches.QueryProjection.cs's
//   "query-join-synthesized-subquery-not-implemented", whose anchor says not-implemented in
//   words yet does not START with "not-yet-implemented", so a [TryFunction] swallows it).
//   Measured and tracked in #2966; classifying them is the work, not deleting the citation.
//
//   #2766 is the separate doubled-link sweep (it measured capital-S "See docs/scope.md"); the
//   doubling is fixed here only as a consequence of rewriting these reason strings.

using AlRunner.Infrastructure;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// The doc section that documents this class of refusal. NOT <c>docs/scope.md</c>: that
    /// file is the permanently-out-of-scope manifest and says nothing about any of these
    /// tables (#2945).
    /// </summary>
    internal const string VirtualTableGapDoc = "docs/limitations.md#virtual-table-shape-gaps";

    /// <summary>
    /// Build the refusal a virtual-table populator raises when BC's shape, the host, or the
    /// runner's own store is not what the populator needs. See this file's header for the
    /// per-site classification and for why the anchor is <c>not-yet-implemented</c> rather
    /// than a <c>docs/scope.md</c> section.
    ///
    /// <para>The table's old anchor is kept as the second token of the reason, so a reader
    /// (and any future expectations entry) can still tell the surfaces apart. No manifest
    /// entry matches on these anchors today — <c>ExpectationManifest.ReasonAnchor</c> cuts at
    /// the first em-dash separator, so the anchor these now report is
    /// <c>not-yet-implemented</c>.</para>
    /// </summary>
    /// <param name="api">The BC surface the AL author touched, e.g. "AllObj (virtual table 2000000038)".</param>
    /// <param name="surface">The table's own anchor, e.g. "allobj-virtual-table".</param>
    /// <param name="detail">What was actually missing — the member that moved, the empty list.</param>
    /// <param name="docLink">Where the reader should look; defaults to the shape-gaps section.</param>
    internal static RunnerOutOfScopeException VirtualTableShapeGap(
        string api, string surface, string detail, string docLink = VirtualTableGapDoc)
        => new(api, $"not-yet-implemented — {surface}: {detail}", docLink);
}
