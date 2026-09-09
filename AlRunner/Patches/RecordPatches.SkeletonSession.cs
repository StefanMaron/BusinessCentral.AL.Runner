// RecordPatches.SkeletonSession — the session/tenant/database state BC's own methods read:
// what NavSession.Database and NavTenant.Database answer, what InitializeSkeletonSession puts
// on a fresh session, and the collation/sorting objects NavDatabase hands out.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;
public static partial class RecordPatches
{
    /// <summary>
    /// Replacement for NavSession.Database getter.
    /// NavSession.Database => Tenant.Database which requires a real tenant.
    /// Return the skeleton NavDatabase instead.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object? NavSession_get_Database(object self)
    {
        return _skeletonDatabase;
    }

    // Cached NavTenant.database (LazyEx<NavDatabase>) field, resolved lazily.
    private static FieldInfo? _fNavTenantDatabase;
    private static bool _fNavTenantDatabaseResolved;

    /// <summary>
    /// Replacement for NavTenant.get_Database. The real getter throws
    /// ArgumentNullException("NavDatabase") when the tenant's `database` LazyEx is null,
    /// which it always is on the skeleton (MetadataPatches leaves it null by design).
    /// Under R2R, `NavSession.Database => Tenant.Database` is inlined past our
    /// NavSession.get_Database redirect, so callers like ALNavApp.ALGetModuleInfo
    /// (FeatureTelemetry.LogUsage during Purch.-Post) reach NavTenant.Database directly.
    /// Return the runner's skeleton NavDatabase (which carries collation/sorting and an
    /// empty-family SqlDatabaseProperties) instead of throwing. If a real database LazyEx
    /// is ever present we honour it. Runtime-engine layer; faithful — the skeleton DOES
    /// have a minimal database, returning it is more correct than throwing.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object? NavTenant_get_Database(object self)
    {
        if (!_fNavTenantDatabaseResolved)
        {
            _fNavTenantDatabase = self?.GetType().GetField("database",
                BindingFlags.NonPublic | BindingFlags.Instance)
                ?? self?.GetType().BaseType?.GetField("database",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            _fNavTenantDatabaseResolved = true;
        }
        if (self != null && _fNavTenantDatabase != null)
        {
            var lazy = _fNavTenantDatabase.GetValue(self);
            if (lazy != null)
            {
                // LazyEx<NavDatabase>.Value — return the real DB if the tenant has one.
                var pValue = lazy.GetType().GetProperty("Value");
                var v = pValue?.GetValue(lazy);
                if (v != null) return v;
            }
        }
        return _skeletonDatabase;
    }

    /// Pre-populate the skeleton session's dataAccessSource field directly.
    /// NavSession.DataAccessSource getter is a trivial field return and gets inlined by JIT,
    /// so the JMP hook on it never fires. We must inject the DAS into the field directly.
    /// </summary>
    public static void InitializeSkeletonSession(object skeletonSession)
    {
        Console.Error.WriteLine($"[RecordPatches] InitializeSkeletonSession: _fSessionDataAccessSource={_fSessionDataAccessSource != null}, _tDataAccessSource={_tDataAccessSource != null}");
        if (_fSessionDataAccessSource == null || _tDataAccessSource == null) return;

        // If already set, nothing to do.
        var existing = _fSessionDataAccessSource.GetValue(skeletonSession);
        Console.Error.WriteLine($"[RecordPatches] existing DAS on session: {existing}");
        if (existing != null) return;

        // Ensure skeleton DB (needed by TempTableDataProvider ctor via navSession.Database.CollationAwareStringComparer)
        EnsureSkeletonDatabase(skeletonSession);

        // Build skeleton DataAccessSource
        var das = RuntimeHelpers.GetUninitializedObject(_tDataAccessSource);
        _fDasSession!.SetValue(das, skeletonSession);
        _fDasGlobalFilters!.SetValue(das, Activator.CreateInstance(_tGlobalFilters!));
        _fDasTableVersionTokens!.SetValue(das, _mCreateForTempTable!.Invoke(null, null));

        // Pre-populate sessionTransactionManager so the lazy-init path in
        // DataAccessSource.get_SessionTransactionManager (which would otherwise call
        // CreateAppDataAccess → CreateAppDataProvider → NRE) is bypassed. The skeleton STM
        // carries a single LogicalTransaction with TransactionType=Update so that the real
        // BC body of ALDatabase.get_ALCurrentTransactionType returns Update without machinery,
        // and SessionTransactionManager.AnyHasWriteTransactionStarted returns false (empty
        // transactionManagers dict — no per-table TM has ever begun a write transaction).
        // Faithful: the runner has no real write transaction system; Update is BC's default
        // for "browsing/reading without a lock-mode override", and IsInWriteTransaction is
        // observably false because nothing has called BeginTransaction.
        if (_skeletonSessionTransactionManager != null && _fDasSessionTransactionManager != null)
            _fDasSessionTransactionManager.SetValue(das, _skeletonSessionTransactionManager);

        // Inject directly into the session field (bypass the inlined getter)
        _fSessionDataAccessSource.SetValue(skeletonSession, das);
        Console.Error.WriteLine($"[RecordPatches] Skeleton DAS injected on session: {das.GetType().Name}");

        // Build a minimal NavSystemCodeunitFactory+GlobalTriggers on the skeleton company so that
        // NavRecord.IsGlobalTriggerImplemented doesn't NRE when it calls
        // Session.SystemCodeunitFactory.GlobalTriggers.GetTriggersOnTable().
        // The factory's GlobalTriggers.session is our skeleton which is not "IsCompanyOpen",
        // so GetTriggersOnTable() returns Triggers.None immediately.
        InjectSkeletonSystemCodeunitFactory(skeletonSession);

        // Populate the auto-property backing field for NavSession.ErrorCollection.
        // The real auto-property `internal ErrorCollection ErrorCollection { get; } = new ErrorCollection();`
        // is initialised in NavSession's instance ctor — which doesn't run for our
        // RuntimeHelpers.GetUninitializedObject skeleton. Without this, NavMethodScope.RunBehaviorAsync
        // NREs the moment any AL method tagged [ErrorBehavior(Collect)] runs (via
        // session.ErrorCollection.StartCollecting()). Construct the real ErrorCollection so
        // StartCollecting / StopCollecting / Collect all execute unmodified BC code (Option C —
        // reuse service-tier code per HANDOFF §2 invariant 4).
        InjectSkeletonErrorCollection(skeletonSession);
    }

    /// <summary>
    /// Build a skeleton SessionTransactionManager whose two read-only getters resolve to
    /// BC-faithful defaults for a session with no real write transaction:
    ///   * CurrentTransactionType => TransactionType.Update (BC's default lock mode)
    ///   * AnyHasWriteTransactionStarted => false (empty per-table TM dict)
    /// Built reflectively via RuntimeHelpers.GetUninitializedObject so we can skip the real
    /// ctor (which wires TransactionManagers tied to TenantDataAccess / AppDataAccess — both
    /// of which require a real connection in our skeleton). All fields the two getters read
    /// are populated explicitly; nothing else is touched.
    /// </summary>
    private static object? BuildSkeletonSessionTransactionManager()
    {
        var nclAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        var typesAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types");
        if (nclAsm == null || typesAsm == null) return null;

        var tStm = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.SessionTransactionManager");
        var tTm = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.TransactionManager");
        var tLt = tTm?.GetNestedType("LogicalTransaction", BindingFlags.NonPublic | BindingFlags.Public);
        var tTrType = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.TransactionType");
        if (tStm == null || tTm == null || tLt == null || tTrType == null) return null;

        // LogicalTransaction is a parameterless internal type whose backing fields are
        // exactly the auto-properties. Construct it and set TransactionType = Update (ordinal 1).
        var lt = Activator.CreateInstance(tLt, nonPublic: true);
        if (lt == null) return null;
        var fLtType = tLt.GetField("<TransactionType>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (fLtType == null) return null;
        var updateValue = Enum.ToObject(tTrType, 1); // TransactionType.Update
        fLtType.SetValue(lt, updateValue);

        // Build a TransactionManager via GetUninitializedObject and populate just the
        // logicalTransactions stack so get_CurrentTransactionType (which Peek()s the stack)
        // returns Update without going through TransactionalDataProvider / NavSession state.
        var tm = RuntimeHelpers.GetUninitializedObject(tTm);
        var fTmLogicalTransactions = tTm.GetField("logicalTransactions",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (fTmLogicalTransactions == null) return null;
        var stackType = typeof(Stack<>).MakeGenericType(tLt);
        var stack = Activator.CreateInstance(stackType)!;
        stackType.GetMethod("Push")!.Invoke(stack, new[] { lt });
        fTmLogicalTransactions.SetValue(tm, stack);

        // Build the SessionTransactionManager and populate:
        //   defaultTransactionManager → our skeleton TM (so STM.CurrentTransactionType works)
        //   transactionManagers       → empty dict (so AnyHasWriteTransactionStarted returns false)
        var stm = RuntimeHelpers.GetUninitializedObject(tStm);
        var fStmDefault = tStm.GetField("defaultTransactionManager",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var fStmDict = tStm.GetField("transactionManagers",
            BindingFlags.NonPublic | BindingFlags.Instance);
        fStmDefault?.SetValue(stm, tm);
        if (fStmDict != null)
        {
            // dict type: ConcurrentDictionary<TransactionManagerKey, TransactionManager>
            var dict = Activator.CreateInstance(fStmDict.FieldType);
            fStmDict.SetValue(stm, dict);
        }

        return stm;
    }

    private static void InjectSkeletonErrorCollection(object skeletonSession)
    {
        var nclAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        if (nclAsm == null) return;
        var tErrorCollection = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.ErrorCollection");
        if (tErrorCollection == null) return;

        var fErrorCollection = skeletonSession.GetType().GetField("<ErrorCollection>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (fErrorCollection == null)
        {
            Console.Error.WriteLine("[RecordPatches] ErrorCollection backing field not found");
            return;
        }
        // Already populated? leave alone.
        if (fErrorCollection.GetValue(skeletonSession) != null)
        {
            WireNavCurrentThreadSession(skeletonSession);
            return;
        }

        // ErrorCollection has only field initialisers (collectedErrors=null, currentCollectionScopeStart=-1)
        // and a default ctor; Activator.CreateInstance runs the field-initialiser block.
        var ec = Activator.CreateInstance(tErrorCollection, nonPublic: true);
        fErrorCollection.SetValue(skeletonSession, ec);
        Console.Error.WriteLine("[RecordPatches] Skeleton ErrorCollection injected");

        WireNavCurrentThreadSession(skeletonSession);
    }

    /// <summary>
    /// Wire NavCurrentThread.Session to return _skeletonSession. NavCurrentThread.Session reads
    /// NavThreadLocalStorage.Current.Session?.Target — an AsyncLocal&lt;IReference&lt;NavSession&gt;&gt;.
    /// Setting it on the bootstrap thread propagates via ExecutionContext into the test threads.
    /// Without this, ALIsCollectingErrors / ALHasCollectedErrors / ALClearCollectedErrors /
    /// ALGetCollectedErrors all dereference NavCurrentThread.Session (null) → NRE; the AL tests
    /// that rely on the [ErrorBehavior(Collect)] surface (CollectThenClear, ClearCollectedErrorsWorks,
    /// CollectMultipleErrors, etc.) all chain through these getters after the collect call returns.
    /// </summary>
    private static void WireNavCurrentThreadSession(object skeletonSession)
    {
        var nclAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        if (nclAsm == null) return;
        var tTLS = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavThreadLocalStorage");
        var tRef = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.Reference`1");
        var tNavSession = skeletonSession.GetType();
        if (tTLS == null || tRef == null) return;

        var pCurrent = tTLS.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
        var current = pCurrent?.GetValue(null);
        if (current == null) return;
        var pSession = tTLS.GetProperty("Session", BindingFlags.Public | BindingFlags.Instance);
        if (pSession == null) return;

        // Already set?
        if (pSession.GetValue(current) != null) return;

        // Build Reference<NavSession>(_skeletonSession). The single-arg ctor is public.
        var refClosed = tRef.MakeGenericType(tNavSession);
        var refInstance = Activator.CreateInstance(refClosed, new[] { skeletonSession });
        pSession.SetValue(current, refInstance);
        Console.Error.WriteLine("[RecordPatches] NavCurrentThread.Session wired to skeleton");
    }

    private static void InjectSkeletonSystemCodeunitFactory(object skeletonSession)
    {
        var nclAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        if (nclAsm == null) return;

        var tFactory = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavSystemCodeunitFactory");
        var tGlobalTriggers = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavSystemCodeunitGlobalTriggers");
        var tNavCompany = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavCompany");
        if (tFactory == null || tGlobalTriggers == null || tNavCompany == null) return;

        // Get the skeleton company from the session.
        var companyField = skeletonSession.GetType().GetField("company",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var skeletonCompany = companyField?.GetValue(skeletonSession);
        if (skeletonCompany == null) return;

        // Build the factory with the REAL public ctor when the skeleton session is a
        // TreeObject (it is — NavSession : TreeObject). A non-null `parent` makes the
        // lazy trigger getters (ReportingTriggers etc.) construct real
        // NavSystemCodeunit instances whose NavCodeunitHandle resolves through the
        // runner's CreateTarget hooks — required for the report-execution chain
        // (GetReportToRun → InvokeSubstituteReport, factory fork →
        // InvokeApplicationReportMergeStrategy, custom merger → OnCustomDocumentMergerEx).
        // Fall back to the old uninitialized skeleton if the ctor shape changed.
        object factory;
        var tTreeObject = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.TreeObject");
        var factoryCtor = tTreeObject != null
            ? tFactory.GetConstructor(new[] { tTreeObject })
            : null;
        if (factoryCtor != null && tTreeObject!.IsInstanceOfType(skeletonSession))
            factory = factoryCtor.Invoke(new[] { skeletonSession });
        else
            factory = RuntimeHelpers.GetUninitializedObject(tFactory);

        // Build GlobalTriggers with its REAL public ctor, NavSystemCodeunitGlobalTriggers(
        // TreeObject parent), parented to the session — same reasoning as the factory above.
        //
        // This used to be a GetUninitializedObject skeleton with `session` field-poked in.
        // That skipped the base NavSystemCodeunit ctor, which is what allocates
        // `codeunitHandle` — the handle BC's own NavGlobalTriggers.Insert/Modify/Delete/
        // RenameAsync invoke the "Global Triggers" codeunit (2000000002) through. Without it
        // no OnDatabase* / OnGlobal* event could ever be published, so AL subscribers to
        // Codeunit::"Global Triggers" silently never fired (corpus CU60210). It also skipped
        // the `triggersOnTables` field initializer, which the old code had to patch up by
        // hand; the real ctor does both correctly.
        object globalTriggers;
        var gtCtor = tGlobalTriggers.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == 1
                && c.GetParameters()[0].ParameterType.IsInstanceOfType(skeletonSession));
        if (gtCtor != null)
        {
            globalTriggers = gtCtor.Invoke(new[] { skeletonSession });
        }
        else
        {
            // Ncl shape changed. Fall back to the old skeleton rather than crash, but say so
            // loudly: on this path every global/database trigger stays silently undelivered.
            Console.Error.WriteLine(
                "[RecordPatches] WARN: NavSystemCodeunitGlobalTriggers(TreeObject) ctor not found — "
                + "falling back to an uninitialized skeleton; AL subscribers to "
                + "Codeunit::\"Global Triggers\" will not fire.");
            globalTriggers = RuntimeHelpers.GetUninitializedObject(tGlobalTriggers);
            var fSession = tGlobalTriggers.GetField("session",
                BindingFlags.NonPublic | BindingFlags.Instance);
            fSession?.SetValue(globalTriggers, skeletonSession);
        }

        // triggersOnTables: Dictionary<int, Triggers> — initialize empty so Monitor.TryEnter
        // doesn't NRE on the locked object. The dict is normally allocated via field initializer
        // which GetUninitializedObject skips.
        var fTriggersOnTables = tGlobalTriggers.GetField("triggersOnTables",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (fTriggersOnTables != null && fTriggersOnTables.GetValue(globalTriggers) == null)
        {
            var dictType = fTriggersOnTables.FieldType;          // Dictionary<int, Triggers>
            fTriggersOnTables.SetValue(globalTriggers, Activator.CreateInstance(dictType));
        }

        // GetTriggersOnTable is BC's own body again — it invokes GetDatabaseTableTriggerSetup
        // on the Global Triggers codeunit, whose AL subscribers decide the per-table mask.

        // Wire global triggers into factory.
        var fGlobalTriggers = tFactory.GetField("globalTriggers",
            BindingFlags.NonPublic | BindingFlags.Instance);
        fGlobalTriggers?.SetValue(factory, globalTriggers);

        // openedDialogRegistry — normally allocated by NavCompany's field initializer,
        // skipped by GetUninitializedObject. NavOpenDialogTracking (report execution:
        // RunReportInternalCoreAsync) pushes onto it → NRE without this.
        var tDialogRegistry = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavOpenedDialogRegistry");
        var fDialogRegistry = tNavCompany.GetField("openedDialogRegistry",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (tDialogRegistry != null && fDialogRegistry != null
            && fDialogRegistry.GetValue(skeletonCompany) == null)
        {
            fDialogRegistry.SetValue(skeletonCompany,
                Activator.CreateInstance(tDialogRegistry, nonPublic: true));
        }

        // The skeleton company has no Tree: it was produced by GetUninitializedObject, so it
        // never ran TreeObject's ctor and was never parented to anything. NavCompany.
        // RegisterFormInternal builds `new NavFormHandle(this, form)` with the COMPANY as
        // parent, and TreeHandler's ctor throws InvalidOperationException("Parent.Tree cannot
        // be null") for a parent with no tree — so registering a form (which BC does before
        // dispatching a modal page to its handler) failed outright.
        //
        // Parenting it to the session is where a real BC server puts it, and going through
        // BC's own CreateTreeHandler means the handler computes its own `session` from the
        // parent exactly as it would there.
        var fTreeObjTree = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.TreeObject")?
            .GetField("tree", BindingFlags.NonPublic | BindingFlags.Instance);
        if (fTreeObjTree != null && fTreeObjTree.GetValue(skeletonCompany) == null)
        {
            var createHandler = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.TreeHandler")?
                .GetMethod("CreateTreeHandler", BindingFlags.Public | BindingFlags.Static);
            if (createHandler != null)
            {
                var companyTree = createHandler.Invoke(null, new[] { skeletonSession, skeletonCompany });
                AlRunner.Infrastructure.FieldPoke.SetInstance(fTreeObjTree, skeletonCompany, companyTree!);
            }
            else
            {
                Console.Error.WriteLine(
                    "[RecordPatches] TreeHandler.CreateTreeHandler NOT FOUND — the skeleton company keeps "
                    + "a null Tree and RegisterForm will throw");
            }
        }

        // registeredForms — same class of gap as openedDialogRegistry above: allocated by
        // NavCompany's ctor (`registeredForms = new Dictionary<Guid, NavFormHandle>()`),
        // which GetUninitializedObject skips. BC's modal-page dispatch registers the form
        // before showing it and unregisters it in a finally, so `lock (registeredForms)`
        // threw ArgumentNullException from inside Monitor.Enter — surfacing to AL as a bare
        // "Value cannot be null." on the RunModal line, naming nothing.
        //
        // A real empty dictionary is the faithful value: a company that has no open forms
        // is exactly the runner's state, and BC populates and drains it itself from here on.
        var fRegisteredForms = tNavCompany.GetField("registeredForms",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (fRegisteredForms != null && fRegisteredForms.GetValue(skeletonCompany) == null)
            fRegisteredForms.SetValue(skeletonCompany,
                Activator.CreateInstance(fRegisteredForms.FieldType));

        // Inject factory into NavCompany.SystemCodeunitFactory auto-property backing field.
        var fFactory = tNavCompany.GetField("<SystemCodeunitFactory>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance);
        fFactory?.SetValue(skeletonCompany, factory);

        // W-8a PR1: populate NavCompany.trackChanges so NavRecord.InsertAsync's
        // `ParentCompany.TrackChanges.TrackChange(...)` call doesn't NRE on the getter.
        // We leave the internal `trackedChanges` dictionary null — TrackChange checks
        // `if (trackedChanges == null) return;` and exits cleanly.
        // (Ref: Ncl NavTrackChanges.TrackChange.)
        var tTrackChanges = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavTrackChanges");
        var fTrackChanges = tNavCompany.GetField("trackChanges",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (tTrackChanges != null && fTrackChanges != null && fTrackChanges.GetValue(skeletonCompany) == null)
        {
            // Prefer BC's REAL ctor, `NavTrackChanges(NavCompany parent)`. It parents the
            // object into the tree and allocates `trackedChanges`, both of which are now
            // required: NavCompany.RegisterFormInternal calls RegisterFormTableChanges, which
            // does `lock (trackedChanges)` with no null guard — unlike TrackChange, which is
            // why the GetUninitializedObject shell below was sufficient until form
            // registration started happening. It only became constructible once the company
            // itself got a Tree (above); before that its base ctor would have thrown.
            object? trackChanges = null;
            var realCtor = tTrackChanges.GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
                binder: null, types: new[] { tNavCompany }, modifiers: null);
            if (realCtor != null)
            {
                try { trackChanges = realCtor.Invoke(new[] { skeletonCompany }); }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                    Console.Error.WriteLine(
                        $"[RecordPatches] NavTrackChanges ctor failed ({inner.GetType().Name}: {inner.Message}) "
                        + "— falling back to an uninitialized shell; form registration will throw");
                }
            }
            // Fallback keeps the previous behaviour rather than leaving the field null.
            trackChanges ??= RuntimeHelpers.GetUninitializedObject(tTrackChanges);
            fTrackChanges.SetValue(skeletonCompany, trackChanges);
        }
    }

    private static void EnsureSkeletonDatabase(object session)
    {
        // _skeletonDatabase is pre-built in Register(); NavSession.Database is JMP-hooked to return it.
        // Nothing to inject on the session object itself.
    }

    private static object? BuildSqlSortingProperties()
    {
        if (_tSqlSortingProperties == null) return null;
        try
        {
            // SqlSortingProperties(CultureInfo culture, CompareOptions compareOptions, string collation)
            var sortingPropsCtor = _tSqlSortingProperties.GetConstructors(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(c => {
                    var ps = c.GetParameters();
                    return ps.Length == 3
                        && ps[0].ParameterType == typeof(System.Globalization.CultureInfo)
                        && ps[1].ParameterType == typeof(System.Globalization.CompareOptions);
                });
            if (sortingPropsCtor == null) return null;
            return sortingPropsCtor.Invoke(new object[] {
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.CompareOptions.IgnoreCase,
                "Latin1_General_CI_AS"
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RecordPatches] BuildSqlSortingProperties failed: {ex.Message}");
            return null;
        }
    }

    private static object? BuildCollationAwareComparer()
    {
        if (_tCollationAwareStringComparer == null || _tSqlSortingProperties == null) return null;
        var sortingProps = _sqlSortingProperties ?? BuildSqlSortingProperties();
        if (sortingProps == null) return null;
        try
        {
            var compCtor = _tCollationAwareStringComparer
                .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(c => c.GetParameters().Length == 1
                    && c.GetParameters()[0].ParameterType == _tSqlSortingProperties);
            return compCtor?.Invoke(new[] { sortingProps });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RecordPatches] BuildCollationAwareComparer failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Replacement for NavSession.get_SortingProperties — Database.SqlSortingProperties NREs because
    /// the skeleton NavDatabase does not have a collation set up for the lazy-init path. Return the
    /// pre-built SqlSortingProperties from RecordPatches.Register.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object? NavSession_get_SortingProperties(object self) => _sqlSortingProperties;

    /// <summary>
    /// Replacement for NavDatabase.CollationAwareStringComparer getter.
    /// Returns a CollationAwareStringComparer using InvariantCulture + IgnoreCase.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object? NavDatabase_get_CollationAwareStringComparer(NavDatabase self)
    {
        // Check if already populated on the skeleton instance (set by EnsureSkeletonDatabase).
        if (_fNavDatabaseCollation != null)
        {
            var existing = _fNavDatabaseCollation.GetValue(self);
            if (existing != null) return existing;
        }
        var built = BuildCollationAwareComparer();
        if (built != null && _fNavDatabaseCollation != null)
            _fNavDatabaseCollation.SetValue(self, built);
        return built;
    }
}
