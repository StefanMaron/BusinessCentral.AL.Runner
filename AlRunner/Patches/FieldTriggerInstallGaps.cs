// FieldTriggerInstallGaps — the field-trigger install path's NON-shape-gap refusals (#3048).
//
// ── WHY A SECOND FILE NEXT TO FieldTriggerShapeGaps.cs ───────────────────────────────────
// #3026 converted seventeen silent skips on this path into named BcShapeGapExceptions, and
// deliberately left two alone. Both are still silent skips, and both are still wrong — but
// neither is a question about BC's layout, so BcShapeGapException is the wrong TYPE for
// either. A shape gap's message tells the reader "the runner could not read BC's internals"
// and its first question is "which BC version produced this?" (docs/limitations.md
// #bc-shape-gaps). For these two that question has no answer, because nothing about the BC
// build on disk is involved:
//
//   * FIELD UNRESOLVABLE — the runner's own metatable does not carry a field number that the
//     runner's own emitted AL declares a field trigger for. Both sides are the runner's.
//   * UNSUPPORTED TRIGGER RETURN TYPE — the AL the runner EMITTED declares a trigger method
//     returning something other than void or ValueTask, which BuildFieldTriggerHandler has no
//     FieldTriggerHandler<T> constructor to wrap. Again: a property of our own output.
//
// Both are IN-SCOPE surfaces the runner has not built an answer for, which is the case
// .claude/rules/loud-failures.md assigns to RunnerOutOfScopeException with the reason anchor
// `not-yet-implemented`. That anchor is load-bearing, not cosmetic: for an AL [TryFunction],
// ApplicationObjectBasePatches.IsPermanentOutOfScope traps a refusal into `false` UNLESS the
// reason starts with `not-yet-implemented`. Under a docs/scope.md anchor a runner gap here
// would read as a clean `if not TryX()`, which is the silent default the rule forbids.
//
// ── WHAT THE `catch { continue; }` WAS ACTUALLY ABSORBING ────────────────────────────────
// Decompiled from the pristine service-tier Microsoft.Dynamics.Nav.Ncl.dll (28.1.49838.54308),
// NCLMetaTable.GetFieldByNo(int fieldNo, bool trapError = false) has exactly three outcomes
// with trapError false: it returns AllFields[idx] (or a DisabledFields entry — also non-null),
// it throws NavNCLFieldNotFoundException naming the field and the table caption, or it throws
// InvalidOperationException("field is null") for a hole in AllFields. It cannot return null,
// so the `if (metaField == null) continue;` that followed each catch was dead code on every
// supported build, and the catch could only ever have been swallowing one of those two throws.
//
// Measured, not assumed: with the three sites instrumented to print instead of skip, the
// al-language corpus (2599 tests) and tests/runner-extras (298 tests) performed 253 table
// wirings covering 2,592 base-table field installs and 55 tableextension field installs, and
// hit NONE of the three. There is no legitimate traffic to tolerate here — which is why the
// fix is a refusal rather than a narrower catch that keeps skipping.
//
// ── WHAT A REFUSAL HERE COSTS ────────────────────────────────────────────────────────────
// WireFieldTriggerHandlersAll runs at bundle load, so a refusal on this path aborts the run
// rather than failing one attributable test (issue #3047 tracks that this is unpinned). That
// is the same trade #3026 accepted for the seventeen shape gaps, and it is the right side of
// it: an abort names the table, the field and the reason, where the skip it replaces produced
// a green suite in which the trigger simply never fired.
//
// See also:
//   .claude/rules/loud-failures.md              — no silent out-of-scope failures
//   docs/limitations.md#runtime-shape-gaps      — the reader-facing write-up
//   AlRunner/Patches/FieldTriggerShapeGaps.cs   — the BC-layout half of the same install path

using System;
using System.Reflection;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

/// <summary>
/// One place the field-trigger install path's <see cref="RunnerOutOfScopeException"/>s are
/// built, so a refusal cannot drift back into a silent skip one call site at a time. See this
/// file's header for the defect and for why these are not <see cref="BcShapeGapException"/>s.
/// </summary>
internal static class FieldTriggerInstallGap
{
    /// <summary>
    /// docs/limitations.md, deliberately, NOT docs/scope.md: both refusals are in-scope gaps,
    /// and citing the scope manifest would assert a permanence that is not true (see
    /// <c>RunnerOutOfScopeException.BuildMessage</c>).
    /// </summary>
    private const string Doc = "docs/limitations.md#runtime-shape-gaps";

    /// <summary>
    /// A field the install loop has a built AL trigger handler for, which the metatable the
    /// runner built for that table does not carry. Raised from BOTH install loops — the base
    /// table's own <c>OnValidate</c>/<c>OnLookup</c> in
    /// <c>RecordPatches.WireFieldTriggerHandlers</c>, and a tableextension's before/after lists
    /// and added-field triggers in <c>WireExtensionValidateHandlers</c> — because both wrote
    /// the same <c>EventTriggerData</c> state through the same <c>GetFieldByNo</c> call and
    /// both swallowed the same exception.
    /// </summary>
    /// <param name="cause">
    /// What <c>GetFieldByNo</c> did, verbatim, so the reader is not left guessing which of the
    /// two failure modes fired.
    /// </param>
    internal static RunnerOutOfScopeException FieldUnresolvable(int tableId, int fieldNo, string cause)
        => new($"AL field trigger installation (table {tableId}, field {fieldNo})",
            "not-yet-implemented — field-trigger-install: this table's AL declares a field trigger "
            + $"for field {fieldNo}, but the metatable the runner built for table {tableId} does not "
            + $"carry that field ({cause}), so the handler cannot be attached and the trigger would "
            + "never fire",
            Doc);

    /// <summary>
    /// An AL trigger method whose return type <c>BuildFieldTriggerHandler</c> has no
    /// <c>FieldTriggerHandler&lt;NavApplicationObjectBase&gt;</c> constructor for. BC closes
    /// that type over exactly two shapes — <c>Action&lt;T&gt;</c> and
    /// <c>Func&lt;T, ValueTask&gt;</c> — and the AL compiler emits a field trigger as one of
    /// those two, so anything else is the runner's own emitted AL in a shape this path has
    /// never been taught to wrap.
    /// </summary>
    internal static RunnerOutOfScopeException UnsupportedTriggerReturnType(MethodInfo target, Type returnType)
        => new($"AL field trigger {target.DeclaringType?.Name}.{target.Name}",
            $"not-yet-implemented — field-trigger-install: the trigger method returns {returnType.Name}, "
            + "and only void and ValueTask can be wrapped in a FieldTriggerHandler, so the handler "
            + "cannot be built and the trigger would never fire",
            Doc);
}
