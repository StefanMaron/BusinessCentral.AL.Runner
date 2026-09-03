// RecordPatches.FeatureKeyVirtualTable — routes the "Feature Key" (2000000211) system table to
// BC's OWN FeatureKeyDataProvider.
//
// WHY THIS EXISTS (issue #2585)
//   Feature Key had no provider here, so GetDataAccessForTableCore fell through to the plain
//   in-memory temp store and every read answered zero rows. Base Application's Feature
//   Management reads this table to choose between a feature's modern and legacy
//   implementation, so an empty table made every feature read as unregistered and the legacy
//   path win, silently.
//
//   The named, checkable consequence: CalcOnlyVisibleFlowFields ships with
//   State = FeatureKeyStateOption.AllUsers — it is ON in real BC. With the table empty, the
//   legacy FlowField path won here instead.
//
// WHY THIS CALLS BC'S OWN PROVIDER INSTEAD OF BUILDING ROWS
//   FeatureKey.BuildFeatureKeys() is a hardcoded static list constructed literally in
//   Microsoft.Dynamics.Nav.Types — around 22 features, no discovery and no subscriber — and
//   the runner already loads the DLL that contains it. FeatureKeyProvider.Features then applies
//   three modifiers, and BC's own code applies them correctly given the state we already have:
//
//     1. ApplyTenantFeatureStateFromDatabase — reads table 2000000210 through NavRecord inside
//        a MaximizedPermissionScope and overrides State per row, returning early when its
//        ALFindSet finds nothing. An empty 2000000210 is the correct "no tenant override" case.
//     2. ApplyFeatureKeyOverride — parses ServerUserSettings.Instance.FeatureKeyOverride, a
//        "+Feature;-Feature" string, empty by default and therefore a no-op.
//     3. ApplyECSConfigurationFiltering — runs only when a feature sets IsConfiguredByECS AND
//        ServerUserSettings.Instance.CopilotApiServicesEnabled is true. It calls an external
//        Copilot ECS service, which is permanently out of scope (docs/scope.md#http).
//
//   So the whole gap was the missing route. Rebuilding the list here would be a second,
//   drifting copy of a list BC already owns — and inserting one hardcoded row to steer a single
//   feature, which is what prompted this issue, is the silent fake .claude/rules/loud-failures.md
//   bars.
//
// READ-ONLY, AND THE WRITE PATH SAYS SO
//   Real BC's Modify rejects changes to nine read-only fields by name, enforces the one-way
//   rule, and writes the new state through to table 2000000210. That write path is NOT
//   implemented here. Rows land in the runner's temp store, whose Modify would accept a write
//   and put it nowhere — a Modify that appears to succeed and does nothing. See the guard in
//   RecordPatches.cs's dispatch comment: this table is populated read-only, and issue #2585
//   tracks the write path.
//
// PRECOMPILED-DLL RESPECT
//   FeatureKeyDataProvider, NavSession and ReadOnlyRecordBuffer are runtime-engine types, which
//   .claude/rules/precompiled-dll-respect.md makes ours to drive. No AL business-logic body is
//   touched: the rows are the ones BC's own provider produces.

using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int FeatureKeyVirtualTableId = 2000000211;

    private static readonly ConditionalWeakTable<object, object> _fkPopulatedProviders = new();

    private static bool _fkReflectionReady;
    private static Type? _fkProviderType;        // Microsoft.Dynamics.Nav.Runtime.FeatureKeyDataProvider
    private static ConstructorInfo? _fkProviderCtor;   // .ctor(NavSession)
    private static MethodInfo? _fkGetAllItems;   // protected IEnumerable<ReadOnlyRecordBuffer> GetAllItems(out bool)

    private static bool IsFeatureKeyVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == FeatureKeyVirtualTableId;

    /// <summary>
    /// Populate the in-memory store behind Feature Key (2000000211) with the rows BC's own
    /// FeatureKeyDataProvider produces. Once per provider: the feature list is fixed for the
    /// lifetime of a run, and the runner does not implement the write-through path that would
    /// change a row's State.
    /// </summary>
    private static void PopulateFeatureKeyVirtualTable(object dataAccess, NCLMetaTable metaTable, object session)
    {
        EnsureAllObjReflection(metaTable);
        EnsureDataAccessProviderReflection(dataAccess);
        EnsureFeatureKeyReflection();

        var store = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw new RunnerOutOfScopeException(
                "Feature Key (system table 2000000211)",
                "feature-key-virtual-table — data access has no in-memory provider; see docs/scope.md");

        if (_fkPopulatedProviders.TryGetValue(store, out _)) return;

        object provider;
        try
        {
            provider = _fkProviderCtor!.Invoke(new object?[] { session });
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            throw new RunnerOutOfScopeException(
                "Feature Key (system table 2000000211)",
                "feature-key-virtual-table — BC's own FeatureKeyDataProvider could not be "
                + $"constructed on the skeleton session ({inner.GetType().Name}: {inner.Message}). "
                + "Its constructor reads the table's own metatable and the session's app group; "
                + "rebuilding the feature list here instead would be a second copy of a list BC "
                + "owns. See docs/scope.md and AlRunner#2585");
        }

        object? rows;
        var args = new object?[] { false };
        try
        {
            rows = _fkGetAllItems!.Invoke(provider, args);
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            throw new RunnerOutOfScopeException(
                "Feature Key (system table 2000000211)",
                "feature-key-virtual-table — BC's own FeatureKeyDataProvider.GetAllItems failed "
                + $"({inner.GetType().Name}: {inner.Message}). Answering with no rows would make "
                + "every feature read as unregistered and silently win the legacy code path, "
                + "which is the bug this fixes. See docs/scope.md and AlRunner#2585");
        }

        var inserted = 0;
        foreach (var buffer in (System.Collections.IEnumerable)rows!)
        {
            if (buffer == null) continue;
            InsertPreBuiltVirtualRow(store, buffer);
            inserted++;
        }

        if (inserted == 0)
            throw new RunnerOutOfScopeException(
                "Feature Key (system table 2000000211)",
                "feature-key-virtual-table — BC's own FeatureKeyDataProvider produced no rows. "
                + "Its feature list is a hardcoded static in Microsoft.Dynamics.Nav.Types, so an "
                + "empty result means a modifier filtered everything out (a tenant state read, "
                + "the FeatureKeyOverride setting, or ECS configuration filtering) rather than "
                + "that BC ships no features. Silently answering empty would put back exactly "
                + "the wrong-legacy-path bug this fixes. See docs/scope.md and AlRunner#2585");

        _fkPopulatedProviders.Add(store, new object());
    }

    /// <summary>
    /// Insert a ReadOnlyRecordBuffer BC's own provider already built. Unlike InsertVirtualRow
    /// there is nothing to fill in — every column is the provider's own value.
    /// </summary>
    private static void InsertPreBuiltVirtualRow(object store, object readOnlyBuffer)
    {
        var mutable = _aovCtorMutableBuffer!.Invoke(new object?[] { readOnlyBuffer });
        try
        {
            _aovTtdpInsert!.Invoke(store, new object?[] { 0, mutable, _aovInsertOptionsNone, null });
        }
        catch (TargetInvocationException tie) when (
            tie.InnerException?.GetType().Name == "NavRecordAlreadyExistsException")
        {
            // Same feature id already present — faithful for a table keyed on it.
        }
    }

    private static void EnsureFeatureKeyReflection()
    {
        if (_fkReflectionReady) return;

        var navNcl = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        _fkProviderType = navNcl?.GetType("Microsoft.Dynamics.Nav.Runtime.FeatureKeyDataProvider");

        _fkProviderCtor = _fkProviderType?.GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == 1
                                 && c.GetParameters()[0].ParameterType.Name == "NavSession");

        _fkGetAllItems = _fkProviderType?.GetMethod("GetAllItems",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        if (_fkProviderType == null || _fkProviderCtor == null || _fkGetAllItems == null)
            throw new RunnerOutOfScopeException(
                "Feature Key (system table 2000000211)",
                "feature-key-virtual-table — BC's FeatureKeyDataProvider does not expose the "
                + $"shape this drives (type={_fkProviderType != null}, "
                + $"ctor(NavSession)={_fkProviderCtor != null}, GetAllItems={_fkGetAllItems != null}). "
                + "A BC shape change says so here rather than being papered over with a "
                + "hand-built feature list. See docs/scope.md");

        _fkReflectionReady = true;
    }
}
