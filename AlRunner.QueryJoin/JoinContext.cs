// JoinContext — the delegate boundary between al-runner.dll and the isolated
// join executor. al-runner builds this (its delegates close over al-runner's own
// reflection helpers / static caches) and hands it to JoinExecutor.Execute.
//
// CRUCIAL: every member here is typed in terms of `object`, `Array`, `Func`/`Action`,
// and BCL primitives ONLY — no Microsoft.Dynamics.Nav.* type appears in any signature.
// That keeps this assembly (and the thin shim in al-runner that references it) free of
// Ncl-referencing IL, so neither perturbs al-runner.dll's startup ReadyToRun binding.
namespace AlRunner.QueryJoin;

using System.Collections;

public sealed class JoinContext
{
    /// <summary>(dataAccessSource, tableMetaObject) → DataAccess (boxed as object).</summary>
    public required Func<object, object, object> GetDataAccessForTable;

    /// <summary>DataAccess → its DataProvider (TempTableDataProvider), or null.</summary>
    public required Func<object, object?> GetDataProvider;

    /// <summary>Warm al-runner's projection reflection against this provider.</summary>
    public required Action<object> EnsureProjectionReflection;

    /// <summary>(provider, findRequest) → table-shaped rows (ReadOnlyRecordBuffers as objects).</summary>
    public required Func<object, object, IEnumerable> FindImplementation;

    /// <summary>Build a FindProviderRequest that full-scans <c>table</c> honouring the dataitem's own filters.</summary>
    public required Func<object /*provider*/, object /*dataItem*/, object /*table*/, object?> BuildFindAllRequest;

    /// <summary>(metaQuery, NavValue[] as Array) → a query-shaped ReadOnlyRecordBuffer (object).</summary>
    public required Func<object, Array, object> MakeReadOnlyRecordBuffer;

    /// <summary>object?[] of NavValues → a typed NavValue[] Array (al-runner owns the NavValue type cache).</summary>
    public required Func<object?[], Array> ToNavValueArray;

    /// <summary>
    /// Produce a typed default NavValue for the given table field (NCLMetaField as object),
    /// boxed as object. Used to fill unmatched LeftOuterJoin child columns with the child
    /// field's typed default instead of leaving the slot null. Returns null if no typed
    /// default can be produced (caller then leaves the slot at its array default).
    /// </summary>
    public required Func<object /*field*/, object?> TypedDefaultForField;

    /// <summary>
    /// (NCLMetaQueryColumn as object, one raw NavValue-or-null per row in the group) →
    /// the aggregated NavValue (boxed as object). Used when the query has an implicit
    /// GROUP BY (issue #2146: a Method = Sum/Count/Average/Min/Max column) — al-runner
    /// owns the actual aggregation math (shared with the single-dataitem GROUP BY path)
    /// so it is not duplicated across this assembly-isolation boundary.
    /// </summary>
    public required Func<object /*column*/, object?[] /*rawValues*/, object?> ComputeAggregate;

    /// <summary>Diagnostic log sink (al-runner's QLog).</summary>
    public required Action<string> Log;

    /// <summary>
    /// Factory for AlRunner.Infrastructure.RunnerOutOfScopeException so the executor can
    /// throw the project's typed OOS exception without referencing al-runner's types directly.
    /// (api, reason) → Exception. Throwing it loudly is required by loud-failures.
    /// </summary>
    public required Func<string, string, Exception> OutOfScope;
}
