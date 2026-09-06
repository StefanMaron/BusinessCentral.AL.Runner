// RecordPatches.PermissionShapeGaps — the permission surface's BC-internals refusals (#2994).
//
// ── WHAT THIS SLICE IS ───────────────────────────────────────────────────────────────────
// #2946 settled the convention: a refusal meaning "the runner could not READ BC's internals"
// raises BcShapeGapException, not RunnerOutOfScopeException and not a hand-rolled
// InvalidOperationException. It converted six readers and deliberately left the rest, because
// reclassifying a site is a per-site judgement rather than a rename. #2994 is the sweep; this
// file is its permission-surface slice, covering three files:
//
//     RecordPatches.AggregatePermissionSetVirtualTable.cs  — Aggregate Permission Set, 2000000167
//     RecordPatches.MetadataPermissionSetVirtualTable.cs   — Metadata Permission Set, 2000000250
//     RecordPatches.PermissionMetadataPopulator.cs         — the NavAppGroup inventory both drive
//
// The populator is in the slice rather than left for later because
// EnsurePermissionMetadataPopulated() has exactly two callers — the AL-entered populate path
// of each of the two tables — and nothing else. Its refusals therefore reach AL on the same
// path the tables' own do, and converting the tables without it would have left one half of a
// single path raising the retired convention.
//
// ── WHY THE TYPE CHANGE IS A CORRECTNESS FIX, NOT A RENAME ───────────────────────────────
// MethodScopePatches.NavMethodScope_AssertError is an unfiltered catch(Exception). So before
// this change, `asserterror <any read of these two tables>` PASSED whenever a BC member behind
// them had moved — while real BC reads the tables fine, so the asserterror fails there.
// Swallowing the refusal did not merely hide a gap, it inverted the result, and green.
// BcShapeGapException tears through both AL seams (see BcShapeGapException.cs's table), and it
// cannot be absorbed by an expect-oos manifest entry — correct, because which BC build is on
// disk is not a scope boundary and can differ between two legs of one matrix run.
//
// ── CLASSIFYING THE 64 SITES ─────────────────────────────────────────────────────────────
// Every refusal in the three files was read and classified before it was touched. The line is
// BcShapeGapException.cs's: RAISE IT when the read could not be performed; DO NOT when the read
// SUCCEEDED and the answer was merely unwelcome.
//
//   converted to BcShapeGapException ........ 56
//   left as they are ......................... 8
//
// The 56 are the reflection resolutions: a type, method, property, field, constructor or nested
// type of BC's own Ncl / Types / Apps assemblies that came back null, plus four sites where the
// member WAS found and holds a shape this code cannot drive — which BcShapeGapException.cs puts
// on the same side of the line as an absent one ("present and holds something of a shape the
// runner cannot use"):
//
//   * groupObjectMetadataSummariesByType is not an array
//   * ... has fewer slots than ObjectType.PermissionSet's ordinal
//   * ObjectType's PermissionSet ordinal means a different object type in this BC build
//   * MetaPermissionSet's include/exclude element type is one BuildIncludeList cannot fill
//
// The last two are worth naming: both are read successfully and both are still gaps, because
// what they return is a statement about BC's LAYOUT that this code cannot act on. They are also
// the ones that can genuinely differ between two supported BC minors, which is precisely the
// property the type exists to report.
//
// ── THE 8 THAT WERE NOT CONVERTED, AND WHY ───────────────────────────────────────────────
// Five keep their InvalidOperationException and three keep their RunnerOutOfScopeException.
// PermissionMetadataShapeGapTests pins all of them, so over-converting fails a test just as
// under-converting does.
//
//   1. AggregatePermissionSetVirtualTable "PermissionSetRecord.permissionSetKey was null"
//      The field was resolved and READ; BC's own record answered null. An answer, not a
//      failure to read one.
//
//   2. PermissionMetadataPopulator "NavAppGroup.BaseGroup is null"
//      Same: the static field resolves, and the value is null. BcShapeGapException.cs names
//      this case exactly — "a skeleton singleton the RUNNER populates is null" — and the
//      existing message already says so ("or the skeleton session was not set up").
//
//   3-5. "Microsoft.Dynamics.Nav.Ncl is not loaded" (x1) and
//        "Microsoft.Dynamics.Nav.Types is not loaded" (x2)
//      An assembly missing from the AppDomain is a fact about the runner's own load chain, not
//      about BC's internal layout. Reporting it as a shape gap would send the reader to
//      docs/limitations.md#bc-shape-gaps, whose first instruction is "ask which BC version
//      produced it" — the wrong question for a provisioning or load-order problem. Nothing in
//      the runner works at all if these are absent.
//
//   6-8. The three VirtualTableShapeGap refusals (RunnerOutOfScopeException, anchor
//        "not-yet-implemented"), classified by #2945 and unchanged here:
//        two "data access has no in-memory provider" (bucket (a): the runner's own store
//        wiring handed no provider over) and one "NavSession.NCLMetadata is null on the
//        skeleton session" (again a skeleton the runner populates). All three are answers
//        about the RUNNER's state, so none is a BC-layout report.
//
// ── DELIBERATELY LEFT FOR LATER ──────────────────────────────────────────────────────────
// #2994 is a ~540-site sweep and this is one slice of it. Not touched here:
//   * RecordPatches.DateVirtualTable.cs and RecordPatches.AllObjVirtualTable.cs — both have
//     open PRs against them (#3028/#2988 and #3004); converting them concurrently would
//     collide. DateVirtualTable's own refusals are already tracked by #2965.
//   * The other virtual tables (Field, Integer, AllProfile, ReportMetadata) and
//     RecordPatches.FieldFindIntercept.cs.
//   * AlRunner/Infrastructure/NclCecilRewrite.* — ~268 sites that fire at REWRITE time, before
//     any AL executes. No AL seam can trap one, so the new type buys nothing there; #2994's own
//     table says "probably not" and this slice agrees.
//
// A note on counting, for whoever takes the next slice: #2994's site count comes from a
// heuristic (a throw within four lines of text saying a member was not found). In these three
// files that heuristic found 49 of the 61 InvalidOperationException sites — it misses wordings
// like "has no backing field", "is not an array", "has no public constructor" and "takes no
// AppId". Treat the issue's 538 as a floor.
//
// See also:
//   AlRunner/Infrastructure/BcShapeGapException.cs   — the derivation and the line
//   RecordPatches.VirtualTableShapeGap.cs            — the sibling classification for #2945
//   docs/limitations.md#bc-shape-gaps                — the reader-facing write-up

using AlRunner.Infrastructure;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// A BC-internals read behind the Aggregate Permission Set virtual table (2000000167) that
    /// could not be performed. See this file's header for the per-site classification.
    /// </summary>
    /// <param name="member">The BC member that could not be read, e.g. "PermissionSetKey.AppId".</param>
    /// <param name="detail">What went wrong and what it costs.</param>
    internal static BcShapeGapException AggregatePermissionSetBcShapeGap(string member, string detail)
        => new("Aggregate Permission Set (virtual table 2000000167)", member, detail);

    /// <summary>
    /// A BC-internals read behind the Metadata Permission Set virtual table (2000000250) that
    /// could not be performed.
    /// </summary>
    internal static BcShapeGapException MetadataPermissionSetBcShapeGap(string member, string detail)
        => new("Metadata Permission Set (virtual table 2000000250)", member, detail);

    /// <summary>
    /// A BC-internals read behind the permission METADATA layer — the NavAppGroup permission-set
    /// inventory both permission virtual tables populate through
    /// <c>EnsurePermissionMetadataPopulated</c> (#2893).
    /// </summary>
    internal static BcShapeGapException PermissionMetadataBcShapeGap(string member, string detail)
        => new("Permission metadata (NavAppGroup permission-set inventory)", member, detail);
}
