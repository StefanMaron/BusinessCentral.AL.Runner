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

    // ── Modify guard: BC's own read-only-field-by-name rejection (#2636) ────────────────
    //
    // FeatureKeyDataProvider.Modify (Ncl) walks its own `FeatureKeyReadOnlyFields` static
    // (every field except "Enabled") and, for the first one whose value differs from the
    // record's current stored value, throws NavCSideException(Lang.InvalidFeatureKeyField,
    // <that field's FieldCaption>) BEFORE it ever writes anything through to table
    // 2000000210. That write-through is not implemented here (#2585's remaining half), but
    // the read-only refusal needs none of it: it only needs to know which field changed and
    // BC's own wording for saying so, both of which are read off BC's own types rather than
    // hand-copied, so a BC version that renumbers or rewords either is followed automatically.
    private static FieldInfo? _fkReadOnlyFieldsField;   // static int[] FeatureKeyReadOnlyFields

    private static Type? _fkLangType;
    private static string? _fkInvalidFieldMessage;      // "...{0}..." from Lang.InvalidFeatureKeyField

    /// <summary>
    /// Prepended (via StampSystemFieldsOnModify) ahead of any Feature Key Modify. Throws BC's
    /// own NavCSideException, naming the field, the moment a read-only column's value differs
    /// from what is currently stored. A Modify that only touches "Enabled" is not blocked here
    /// — this guard makes no claim about the write-through path itself.
    /// </summary>
    internal static void GuardFeatureKeyReadOnlyFieldsOnModify(NavRecord self)
    {
        var meta = self.MetaTable
            ?? throw new RunnerOutOfScopeException(
                "Feature Key (system table 2000000211) — Modify",
                "feature-key-modify — record carries no metatable; see docs/scope.md");

        var readOnlyFieldNos = ReadOnlyFieldNumbers();

        var keyField = meta.PrimaryKey?.GetKeyFieldByIndex(0)
            ?? throw new RunnerOutOfScopeException(
                "Feature Key (system table 2000000211) — Modify",
                "feature-key-modify — metatable has no primary key field; see docs/scope.md");
        var keyValue = self.GetFieldValue(keyField.FieldNo);

        var original = new NavRecord(self.ParentSession, FeatureKeyVirtualTableId);
        try
        {
            if (!original.ALGet(Microsoft.Dynamics.Nav.Types.DataError.TrapError, keyValue))
                return; // no stored row to compare against — let the ordinary write path decide
        }
        finally
        {
            original.Dispose();
        }

        foreach (var fieldNo in readOnlyFieldNos)
        {
            if (fieldNo == keyField.FieldNo) continue; // the key value is how we found the row
            var currentValue = self.GetFieldValue(fieldNo);
            var originalValue = original.GetFieldValue(fieldNo);
            if (Equals(currentValue, originalValue)) continue;

            var caption = meta.GetFieldByNo(fieldNo)?.FieldCaption ?? $"field {fieldNo}";
            throw BuildFeatureKeyReadOnlyError(caption);
        }
    }

    private static int[] ReadOnlyFieldNumbers()
    {
        EnsureFeatureKeyReflection();
        _fkReadOnlyFieldsField ??= _fkProviderType!.GetField(
            "FeatureKeyReadOnlyFields", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new RunnerOutOfScopeException(
                "Feature Key (system table 2000000211) — Modify",
                "feature-key-modify — BC's FeatureKeyDataProvider no longer declares "
                + "FeatureKeyReadOnlyFields; see docs/scope.md");

        return (int[])(_fkReadOnlyFieldsField.GetValue(null)
            ?? throw new RunnerOutOfScopeException(
                "Feature Key (system table 2000000211) — Modify",
                "feature-key-modify — FeatureKeyReadOnlyFields is null; see docs/scope.md"));
    }

    private static Exception BuildFeatureKeyReadOnlyError(string fieldCaption)
    {
        var format = InvalidFeatureKeyFieldMessage();
        var message = string.Format(System.Globalization.CultureInfo.CurrentCulture, format, fieldCaption);

        var navCSideExceptionType = typeof(NavRecord).Assembly.GetType(
            "Microsoft.Dynamics.Nav.Runtime.NavCSideException");
        if (navCSideExceptionType != null
            && Activator.CreateInstance(navCSideExceptionType, message) is Exception typed)
            return typed;

        // Never swallow the refusal: an untyped exception still stops the write and still
        // carries BC's message, which is what AL's asserterror observes.
        return new InvalidOperationException(message);
    }

    /// <summary>
    /// BC's own "Sorry, but the task couldn't be completed because it tried to change the
    /// "{0}" field of the feature key, which cannot be changed. ..." resource string, read
    /// off Ncl's resx-generated Lang type rather than restated here, so a BC version that
    /// rewords it is followed automatically instead of drifting.
    /// </summary>
    private static string InvalidFeatureKeyFieldMessage()
    {
        if (_fkInvalidFieldMessage != null) return _fkInvalidFieldMessage;

        _fkLangType ??= FindFeatureKeyLangType()
            ?? throw new RunnerOutOfScopeException(
                "Feature Key (system table 2000000211) — Modify",
                "feature-key-modify — Ncl's InvalidFeatureKeyField message resource could not be "
                + "located; see docs/scope.md");

        var prop = _fkLangType.GetProperty("InvalidFeatureKeyField",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new RunnerOutOfScopeException(
                "Feature Key (system table 2000000211) — Modify",
                "feature-key-modify — Ncl states no InvalidFeatureKeyField message resource; "
                + "see docs/scope.md");

        var text = prop.GetValue(null) as string
            ?? throw new RunnerOutOfScopeException(
                "Feature Key (system table 2000000211) — Modify",
                "feature-key-modify — Ncl's InvalidFeatureKeyField message resource is empty; "
                + "see docs/scope.md");

        _fkInvalidFieldMessage = text;
        return text;
    }

    /// <summary>
    /// Ncl's resx-generated resource class, found by the presence of the very property this
    /// guard needs — the same lookup shape AllProfileWritePatches uses, since a plain
    /// Assembly.GetTypes() call over the BC assemblies routinely throws
    /// ReflectionTypeLoadException on the skeleton runtime.
    /// </summary>
    private static Type? FindFeatureKeyLangType()
    {
        foreach (var asm in new[] { typeof(NavRecord).Assembly }
                     .Concat(AppDomain.CurrentDomain.GetAssemblies()
                         .Where(a => a.GetName().Name?.StartsWith(
                             "Microsoft.Dynamics.Nav", StringComparison.Ordinal) == true))
                     .Distinct())
        {
            Type?[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            catch { continue; }

            foreach (var t in types)
            {
                if (t == null) continue;
                if (t.GetProperty("InvalidFeatureKeyField",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) != null)
                    return t;
            }
        }
        return null;
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
