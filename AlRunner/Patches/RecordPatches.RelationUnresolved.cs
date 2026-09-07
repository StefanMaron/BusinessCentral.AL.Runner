using System.Collections.Concurrent;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

/// <summary>
/// Bookkeeping for a field whose <c>TableRelation</c> names something the runner could not
/// resolve — issue #3306, and the exact sibling of
/// <see cref="RecordPatches"/>'s CalcFormula bookkeeping in
/// <c>RecordPatches.CalcFormulaUnresolved.cs</c> (#3279/#3263).
///
/// <para><c>BuildMetaFieldRelations</c> answers <c>null</c> — dropping the WHOLE relation — when
/// an arm's target table, if()-condition field, where() field or where()-<c>field()</c> link
/// does not resolve. Dropping is the right call for the metadata itself: a half-built arm would
/// propagate renames real BC does not, and <c>fieldId 0</c> means "the primary key", so guessing
/// is worse than refusing. What was wrong is what happened NEXT.</para>
///
/// <para>A <c>null</c> relation array reaches BC's <c>NCLMetaField</c> ctor as
/// <c>EmptyFieldRelations</c>, and <c>RecordImplementation.EvaluateRelation</c> answers
/// <c>-1</c> for that — the same answer it gives for the entirely ordinary "no arm applies to
/// this row". BC's two consumers then take their silent defaults:</para>
///
/// <list type="bullet">
/// <item><c>ValidateNonFlowFieldAsync</c> skips its <c>ValidateRelation</c> call entirely, so
/// <c>Validate</c> ACCEPTS a value with no row in the related table — where real BC raises
/// "&lt;value&gt; cannot be found in the related table".</item>
/// <item><c>RecordImplementation.GetRelation</c> maps <c>-1</c> to <c>0</c>, so
/// <c>FieldRef.Relation</c> answers 0 — indistinguishable from "this field declares no
/// TableRelation at all".</item>
/// </list>
///
/// <para>Both are silent WRONG ANSWERS in the direction that looks like success, and the only
/// trace was a <c>[RecordPatches]</c> line that default verbosity drops — precisely the failure
/// <c>.claude/rules/loud-failures.md</c> exists to prevent. The reason is now recorded here
/// against the field, and <see cref="RecordPatches.RecordImpl_UnresolvedRelationGuardForEvaluate"/>
/// refuses at the seam AL reaches instead of letting BC answer a default.</para>
///
/// <para><b>Why not refuse in the builder.</b> A throw inside <c>BuildMetaFieldRelations</c>
/// makes the whole TABLE unbuildable because one arm of one field did not resolve — every test
/// touching that table dies, including the ones that never read the field. #3279 faced the
/// identical choice on the CalcFormula side and resolved it the same way: record at build time,
/// refuse at the seam. Issue #3306 names this as one of the two things to settle, and this is
/// the answer.</para>
/// </summary>
public static partial class RecordPatches
{
    /// <summary>
    /// (owning table id, field id) → what could not be resolved. A
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> because metatables are built lazily on
    /// whichever thread first touches the table.
    /// </summary>
    private static readonly ConcurrentDictionary<(int TableId, int FieldId), string>
        _unresolvedRelationRefs = new();

    private static void NoteUnresolvedRelationReference(int tableId, int fieldId, string reason)
        => _unresolvedRelationRefs[(tableId, fieldId)] = reason;

    private static void ClearUnresolvedRelationReference(int tableId, int fieldId)
        => _unresolvedRelationRefs.TryRemove((tableId, fieldId), out _);

    /// <summary>Drop every note. Called from <c>ResetForReload</c>, where every table is rebuilt
    /// from scratch and a note from the previous bundle would refuse a field the new one
    /// resolves.</summary>
    internal static void ClearUnresolvedRelationReferences()
        => _unresolvedRelationRefs.Clear();

    /// <summary>
    /// The reason the field <paramref name="fieldId"/> on table <paramref name="tableId"/> has
    /// no relations, when the runner is the one that could not resolve them. False means the
    /// runner never built a relation for that field — a field genuinely declaring no
    /// <c>TableRelation</c>, which is the overwhelming majority and whose <c>-1</c> / <c>0</c>
    /// is BC's own correct answer.
    /// </summary>
    internal static bool TryGetUnresolvedRelationReference(int tableId, int fieldId, out string reason)
        => _unresolvedRelationRefs.TryGetValue((tableId, fieldId), out reason!);

    /// <summary>
    /// Prepended to <c>RecordImplementation.EvaluateRelation(NCLMetaField)</c> — the single
    /// point every AL-observable relation read converges on.
    ///
    /// <para>That it is a single point is read out of Ncl.dll rather than assumed: the method
    /// has exactly three callers, and they are the three routes AL can take —</para>
    ///
    /// <list type="bullet">
    /// <item><c>NavRecord.EvaluateRelation(int)</c> — the public entry, reached by
    /// <c>AutofillHelper</c>;</item>
    /// <item><c>RecordImplementation.GetRelation(NCLMetaField)</c> — what
    /// <c>FieldRef.Relation</c> answers with;</item>
    /// <item><c>RecordImplementation.&lt;ValidateNonFlowFieldAsync&gt;d__160.MoveNext</c> — the
    /// relation check inside <c>Validate</c>.</item>
    /// </list>
    ///
    /// <para>So one guard covers <c>Validate</c>, <c>FieldRef.Relation</c> and autofill, and no
    /// fourth route can slip past it. Guarding the two public consumers separately would have
    /// left the third uncovered and would have had to be kept in sync by hand.</para>
    ///
    /// <para><paramref name="self"/> is the <c>RecordImplementation</c> the prepend forwards
    /// from IL slot 0 and is deliberately unused: the decision depends only on WHICH FIELD is
    /// being evaluated, never on the record's state. It is in the signature because
    /// <c>PrependStaticCall</c> forwards IL arg slots from the front of the list, so reaching
    /// slot 1 means accepting slot 0.</para>
    ///
    /// <para><b>Cost.</b> One <see cref="ConcurrentDictionary{TKey,TValue}"/> lookup, and only
    /// when the dictionary is non-empty — the <see cref="ConcurrentDictionary{TKey,TValue}.IsEmpty"/>
    /// check short-circuits every call in the overwhelmingly common case where nothing failed to
    /// resolve. Measured on this tree, that case is universal: with the four drop sites
    /// instrumented to print unconditionally, the whole al-language corpus (2,700 tests across
    /// three app roots) and the whole of <c>tests/runner-extras</c> produced ZERO drops, so the
    /// dictionary stays empty for both suites end to end.</para>
    /// </summary>
    public static void RecordImpl_UnresolvedRelationGuardForEvaluate(object? self, NCLMetaField? field)
    {
        // The fast path, and it is the only path in every measured run: nothing was ever
        // recorded, so there is nothing any field could match.
        if (_unresolvedRelationRefs.IsEmpty) return;
        if (field == null) return;
        if (field.Parent is not NCLMetaTable parent) return;
        if (!TryGetUnresolvedRelationReference(parent.TableId, field.FieldNo, out var reason)) return;

        // A RunnerOutOfScopeException with a `not-yet-implemented` reason, the same channel
        // ThrowIfColumnHasNoSource and ThrowIfCalcFormulaReferenceUnresolved use.
        //
        // `not-yet-implemented` is load-bearing rather than cosmetic:
        // ApplicationObjectBasePatches.IsPermanentOutOfScope traps a PERMANENTLY out-of-scope
        // refusal into `false` for an AL [TryFunction]. Real BC resolves these names — the gap
        // is the runner's — so trapping would turn this refusal back into the silent default it
        // exists to replace.
        throw new RunnerOutOfScopeException(
            $"TableRelation on \"{field.FieldName}\" in table {parent.TableId} \"{parent.TableName}\"",
            "not-yet-implemented — tablerelation-reference-unresolved — the field declares a "
            + $"TableRelation, but {reason}, so no relation was built; evaluating it would let "
            + "Validate accept a value with no row in the related table, and would answer "
            + "FieldRef.Relation with 0 as though no TableRelation were declared");
    }
}
