using System.Collections.Concurrent;

namespace AlRunner.Patches;

/// <summary>
/// Bookkeeping for a FlowField whose <c>CalcFormula</c> names something the runner could not
/// resolve — issue #3263.
///
/// <para><see cref="RecordPatches.BuildMetaCalcFormula"/> used to <c>continue</c> past a
/// where-arm it could not resolve and build a formula out of the arms that were left. AL then
/// got a number computed with FEWER filters than its own declaration states — the unfiltered
/// total the dropped arm was supposed to narrow — and the only trace was a
/// <c>[RecordPatches]</c> line that default verbosity drops. It now fails closed instead: the
/// formula is not built at all, and the reason is recorded here against the FlowField so
/// <c>CalcFields</c> can say what was unresolvable rather than raising BC's own "You must
/// define a CalcFormula for the {0} FlowField in the {1} table", which points the AL author at
/// a declaration that is already correct.</para>
/// </summary>
public static partial class RecordPatches
{
    /// <summary>
    /// (owning table id, FlowField id) → what could not be resolved. A
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> because metatables are built lazily on
    /// whichever thread first touches the table.
    /// </summary>
    private static readonly ConcurrentDictionary<(int TableId, int FieldId), string>
        _unresolvedCalcFormulaRefs = new();

    private static void NoteUnresolvedCalcFormulaReference(int tableId, int fieldId, string reason)
        => _unresolvedCalcFormulaRefs[(tableId, fieldId)] = reason;

    private static void ClearUnresolvedCalcFormulaReference(int tableId, int fieldId)
        => _unresolvedCalcFormulaRefs.TryRemove((tableId, fieldId), out _);

    /// <summary>Drop every note. Called from <see cref="ResetForReload"/>, where every table is
    /// rebuilt from scratch and a note from the previous bundle would refuse a FlowField the new
    /// one resolves.</summary>
    internal static void ClearUnresolvedCalcFormulaReferences()
        => _unresolvedCalcFormulaRefs.Clear();

    /// <summary>
    /// The reason the FlowField <paramref name="fieldId"/> on table <paramref name="tableId"/>
    /// has no CalcFormula, when the runner is the one that could not resolve it. False means
    /// the runner never tried to build a formula for that field — a genuinely formula-less
    /// FlowField, which is BC's own refusal to raise.
    /// </summary>
    internal static bool TryGetUnresolvedCalcFormulaReference(int tableId, int fieldId, out string reason)
        => _unresolvedCalcFormulaRefs.TryGetValue((tableId, fieldId), out reason!);
}
