// MetadataPatches — manufactures skeleton NavSystemTenant + NCLMetadata, injects them
// into the real NavTenantCollection so NavGlobal.NCLMetadata, NavGlobal.SystemTenant,
// and the chain of NavGlobal.* getters return non-null objects rather than NRE'ing on
// `Tenants.SystemTenant == null`. Field-poke based: no method bodies are rewritten.
//
// Field NREs *inside* NCLMetadata are then patched iteratively: when a corpus call site
// dereferences a null field on the skeleton, FieldPoke a sane default in the static init
// below.
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using JmpHook = AlRunner.Infrastructure.JmpHook;

namespace AlRunner;

public static partial class BcRuntime
{
    private static object? _skeletonNCLMetadata;
    private static object? _skeletonSystemTenant;
    private static Type? _systemTenantTypeForSeed;
    private static bool _metadataProviderSeeded;

    // Test-execution sandbox seam (step 4c below + EnterTestExecutionScope/LeaveTestExecutionScope).
    // Resolved once in InjectSkeletonSystemTenant; null means the seam is unavailable and the two
    // methods below are no-ops (IsSandbox() then keeps the tenant-settings Production default).
    private static System.Reflection.PropertyInfo? _piTestExecution;
    private static object? _testExecutionInstance;
    private static System.Reflection.FieldInfo? _fExecutingTestCodeUnit;
    private static System.Reflection.MethodInfo? _mSetTestTenantEnvironmentType;
    private static bool _testTenantEnvironmentTypeSet;

    /// <summary>
    /// Called by TestExecutor immediately before invoking an AL [Test] method. Mirrors real BC's
    /// NavTestExecution.EnterTestCodeunit: field-pokes `executingTestCodeUnit` to the actual,
    /// concrete NavTestCodeunit-derived instance under test (TestExecutor already instantiated it
    /// via the codeunit's own ITreeObject ctor — it is a real object, never a synthetic sentinel),
    /// which flips `NavTestExecution.InTest` (`executingTestCodeUnit != null`) true for exactly the
    /// duration of the test, exactly as real BC's service-tier test harness does per test codeunit.
    ///
    /// On the FIRST successful entry only, additionally calls
    /// Microsoft.Dynamics.Nav.NavUserAccount.NavTenantSettingsHelper.SetTestTenantEnvironmentType(true)
    /// — the same call real BC's harness makes once per test session — which requires InTest==true
    /// (hence calling it only after the field-poke above) and sets a private static tuple that,
    /// once non-null, is consulted by IsSandbox() for the rest of the process regardless of InTest;
    /// LeaveTestExecutionScope only needs to undo the per-test InTest flag, never that tuple.
    ///
    /// Net effect: NavTenantSettingsHelper.IsSandbox()/IsProduction() (reached via Codeunit 457
    /// "Environment Information" -> 3702 "Environment Information Impl.") report sandbox=true for
    /// the duration of every AL test, and fall back to the faithful tenant-settings Production
    /// default the instant no test is executing — matching real BC, where "test execution never
    /// observes a production tenant" is a standing guarantee ISV apps (e.g. Pageworks) rely on.
    /// Skeleton-state populate + one documented call into an MS DLL's own public seam designed for
    /// exactly this — no method body is rewritten. See .claude/rules/precompiled-dll-respect.md.
    /// </summary>
    public static void EnterTestExecutionScope(object testCodeunitInstance)
        => EnterTestExecutionScope(testCodeunitInstance, null);

    /// <summary>
    /// As above, and additionally mirrors BC's EnterTestMethod by poking
    /// <c>executingTestMethod</c> / <c>executingTestAttr</c> for <paramref name="testMethod"/>.
    ///
    /// Those two are what NavTestExecution.FindHandler reads: it takes the handler NAMES from
    /// <c>executingTestAttr.Handlers</c> (the AL compiler puts [HandlerFunctions(...)] there)
    /// and resolves each against <c>executingTestMethod.DeclaringType</c>. With both null it
    /// returns null immediately, so TestHandleModalForm answered "not handled" and BC fell
    /// through to the real client callback — which does not exist here. A modal page opened
    /// from AL therefore never reached its [ModalPageHandler]; thirty Pageworks tests declare
    /// HandlerFunctions.
    /// </summary>
    public static void EnterTestExecutionScope(object testCodeunitInstance, MethodInfo? testMethod)
    {
        if (_testExecutionInstance == null || _fExecutingTestCodeUnit == null) return;
        try
        {
            FieldPoke.SetInstance(_fExecutingTestCodeUnit, _testExecutionInstance, testCodeunitInstance);
            SetExecutingTestMethod(testMethod);
            if (!_testTenantEnvironmentTypeSet && _mSetTestTenantEnvironmentType != null)
            {
                _mSetTestTenantEnvironmentType.Invoke(null, new object[] { true });
                _testTenantEnvironmentTypeSet = true;
            }
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            Console.Error.WriteLine($"[BcRuntime] EnterTestExecutionScope: sandbox seam failed ({inner.GetType().Name}: {inner.Message}) — IsSandbox() stays Production-default for this test");
        }
    }

    /// <summary>
    /// Called by TestExecutor after a test method returns/throws/times out. Mirrors real BC's
    /// NavTestExecution.LeaveTestCodeunit: clears `executingTestCodeUnit` back to null so `InTest`
    /// (and therefore IsSandbox()'s test-harness branch) is false again outside test execution —
    /// only the per-test flag is undone; the process-lifetime SetTestTenantEnvironmentType(true)
    /// tuple set by EnterTestExecutionScope's first call is intentionally left in place.
    /// </summary>
    public static void LeaveTestExecutionScope()
    {
        if (_testExecutionInstance == null || _fExecutingTestCodeUnit == null) return;
        try
        {
            SetExecutingTestMethod(null);
            FieldPoke.SetInstance(_fExecutingTestCodeUnit, _testExecutionInstance, null);
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            Console.Error.WriteLine($"[BcRuntime] LeaveTestExecutionScope: failed ({inner.GetType().Name}: {inner.Message})");
        }
    }

    private static System.Reflection.FieldInfo? _fExecutingTestMethod;
    private static System.Reflection.FieldInfo? _fExecutingTestAttr;
    private static bool _executingTestMethodFieldsResolved;

    /// <summary>
    /// Poke NavTestExecution's executingTestMethod/executingTestAttr — BC's own EnterTestMethod
    /// (and LeaveTestMethod, when <paramref name="testMethod"/> is null). The attribute is read
    /// off the method itself: the AL compiler emits [HandlerFunctions('A,B')] as
    /// NavTestAttribute.Handlers, so nothing here parses or re-derives the handler list.
    /// </summary>
    private static void SetExecutingTestMethod(MethodInfo? testMethod)
    {
        if (_testExecutionInstance == null) return;
        if (!_executingTestMethodFieldsResolved)
        {
            _executingTestMethodFieldsResolved = true;
            var t = _testExecutionInstance.GetType();
            _fExecutingTestMethod = t.GetField("executingTestMethod", BindingFlags.NonPublic | BindingFlags.Instance);
            _fExecutingTestAttr = t.GetField("executingTestAttr", BindingFlags.NonPublic | BindingFlags.Instance);
            if (_fExecutingTestMethod == null || _fExecutingTestAttr == null)
                Console.Error.WriteLine(
                    "[BcRuntime] NavTestExecution.executingTestMethod/executingTestAttr NOT FOUND — "
                    + "[HandlerFunctions] dispatch will not work");
        }
        if (_fExecutingTestMethod == null || _fExecutingTestAttr == null) return;

        object? attr = null;
        if (testMethod != null)
            attr = testMethod.GetCustomAttributes(inherit: false)
                .FirstOrDefault(a => _fExecutingTestAttr.FieldType.IsInstanceOfType(a));

        FieldPoke.SetInstance(_fExecutingTestMethod, _testExecutionInstance, testMethod);
        FieldPoke.SetInstance(_fExecutingTestAttr, _testExecutionInstance, attr);

        // BC's own seam: InitHandlers copies executingTestAttr.Handlers into the
        // executingHandlers worklist that FindHandler consumes (each match is struck off via
        // RemoveHandlerName). Without it that worklist is null and the first successful
        // handler lookup NREs inside RemoveHandlerName. Calling BC's method rather than
        // poking the string keeps the list format BC's business, not ours.
        if (testMethod != null) InvokeTestExecutionMethod("InitHandlers");
    }

    /// <summary>
    /// BC's check that every handler a test DECLARED actually ran (NavTestExecution.
    /// TestHandlers, which throws NavNCLUnexecutedUIHandlerException naming the leftovers).
    ///
    /// NOT called yet. Wiring it revealed three al-language corpus tests that declare a
    /// Confirm/Notification handler the runner never dispatches to — they pass today only
    /// because the unconsumed handler goes unnoticed. Those are real gaps in a different
    /// subsystem (dialog dispatch, not page dispatch); turning them into hard errors before
    /// that subsystem exists would just make the corpus gate noisy. Call this once
    /// Confirm/Notification/StrMenu handlers are dispatched.
    /// </summary>
    public static void CheckAllHandlersConsumed() => InvokeTestExecutionMethod("TestHandlers");

    private static void InvokeTestExecutionMethod(string name)
    {
        if (_testExecutionInstance == null) return;
        var method = _testExecutionInstance.GetType()
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        if (method == null)
        {
            Console.Error.WriteLine($"[BcRuntime] NavTestExecution.{name} NOT FOUND — Ncl shape changed");
            return;
        }
        try { method.Invoke(_testExecutionInstance, null); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            // BC's own diagnostic (e.g. "handler was not executed") — it is the test's
            // outcome, so it must reach the test rather than be reported as runner noise.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
        }
    }

    /// <summary>
    /// Lazily seed the skeleton NavSystemTenant.metadataProvider with a real MetadataProvider —
    /// exactly what NavSystemTenant's own ctor does (`metadataProvider = new MetadataProvider();`).
    /// We skipped that ctor via GetUninitializedObject, so NavGlobal.MetadataProvider
    /// (=> SystemTenant.MetadataProvider) is null. The virtual-Field data provider
    /// (FieldDataProvider) derives from MetadataDataProvider whose ctor ThrowIfNull's the provider,
    /// so it cannot construct without this. Called the FIRST time the virtual Field table is
    /// accessed (never eagerly), so non-Field-table tests see baseline NavGlobal state.
    /// </summary>
    public static void EnsureMetadataProviderSeeded()
    {
        if (_metadataProviderSeeded) return;
        _metadataProviderSeeded = true;
        var systemTenantType = _systemTenantTypeForSeed;
        if (systemTenantType == null || _skeletonSystemTenant == null) return;
        var stMetaProvField = systemTenantType.GetField("metadataProvider", BindingFlags.NonPublic | BindingFlags.Instance);
        if (stMetaProvField == null)
        {
            Console.Error.WriteLine("[BcRuntime] EnsureMetadataProviderSeeded: NavSystemTenant.metadataProvider field NOT FOUND — virtual Field table will fail");
            return;
        }
        // Only seed if currently null (don't clobber a real provider).
        if (stMetaProvField.GetValue(_skeletonSystemTenant) != null) return;
        try
        {
            var metaProvType = stMetaProvField.FieldType; // Microsoft.Dynamics.Nav.XmlMetadata.MetadataProvider
            var metaProvCtor = metaProvType.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            var metaProv = metaProvCtor != null
                ? metaProvCtor.Invoke(null)
                : RuntimeHelpers.GetUninitializedObject(metaProvType);
            FieldPoke.SetInstance(stMetaProvField, _skeletonSystemTenant, metaProv);
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            Console.Error.WriteLine($"[BcRuntime] EnsureMetadataProviderSeeded: MetadataProvider seed failed ({inner.GetType().Name}: {inner.Message}); falling back to uninitialised instance");
            FieldPoke.SetInstance(stMetaProvField, _skeletonSystemTenant,
                RuntimeHelpers.GetUninitializedObject(stMetaProvField.FieldType));
        }
    }

    /// <summary>Exposes the manufactured skeleton NCLMetadata so other patch files can
    /// FieldPoke into its caches (e.g. populate per-table NCLMetaTable entries).</summary>
    public static object? SkeletonNCLMetadata => _skeletonNCLMetadata;

    /// <summary>The skeleton NavSystemTenant (carries the skeleton NCLMetadata + tenant
    /// settings). Exposed so report-execution sessions can be given a non-null
    /// <c>systemTenant</c> — see NavReportSync.SeedSessionSystemTenant.</summary>
    public static object? SkeletonSystemTenant => _skeletonSystemTenant;

    /// <summary>
    /// Called from ApplyAllPatches *after* the real NavEnvironment ctor has run successfully
    /// (`InstantiateStandaloneNavEnvironment(true,false)`). At that point
    /// <c>NavEnvironment.Instance.Tenants</c> is a real, non-null <c>NavTenantCollection</c> —
    /// but its <c>systemTenant</c> field is null because <c>AddSystemTenant</c> requires a real
    /// SQL connection. We manufacture a skeleton via <c>GetUninitializedObject</c> and write it
    /// into the field directly.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void InjectSkeletonSystemTenant(Assembly navNcl)
    {
        var nclMetadataType    = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetadata");
        var systemTenantType   = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavSystemTenant");
        var navTenantType      = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavTenant");
        var envType            = _navEnvironmentType!;
        if (nclMetadataType == null || systemTenantType == null || navTenantType == null)
        {
            Console.Error.WriteLine("[BcRuntime] InjectSkeletonSystemTenant: type lookup failed");
            return;
        }

        // 1. Build skeleton NCLMetadata (no ctor call — its ctor needs NavDatabase).
        _skeletonNCLMetadata = RuntimeHelpers.GetUninitializedObject(nclMetadataType);

        // GetEntryDictionary() walks `metadataCacheEntries[(int)objectType]`. With null arrays,
        // every call NREs at `dictionaries.Length`. Populate both with empty ConcurrentDictionary
        // entries sized to the ObjectType enum so callers get a defined "not in cache" path —
        // which translates to `NavNCLApplicationObjectNotFoundException` rather than NRE.
        var navTypesAsm0 = AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types");
        var objectTypeEnum = navTypesAsm0.GetType("Microsoft.Dynamics.Nav.Types.ObjectType");
        var enumSize = objectTypeEnum != null ? Enum.GetValues(objectTypeEnum).Length : 27;

        void PopulateCacheArray(string fieldName, Type entryValueType)
        {
            var f = nclMetadataType.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return;
            var dictType = typeof(System.Collections.Concurrent.ConcurrentDictionary<,>)
                .MakeGenericType(typeof(int), entryValueType);
            var arr = Array.CreateInstance(dictType, enumSize);
            for (int i = 0; i < enumSize; i++)
                arr.SetValue(Activator.CreateInstance(dictType), i);
            FieldPoke.SetInstance(f, _skeletonNCLMetadata, arr);
        }
        var entryT  = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetadataCacheEntry");
        var extEntT = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetadataExtensionCacheEntry");
        if (entryT  != null) PopulateCacheArray("metadataCacheEntries",          entryT);
        if (extEntT != null) PopulateCacheArray("metadataExtensionCacheEntries", extEntT);

        // 2. Build skeleton NavSystemTenant.
        _skeletonSystemTenant = RuntimeHelpers.GetUninitializedObject(systemTenantType);

        // 3. Make NavTenant.IsDisposed return false on the skeleton: requires `disposed=false`
        //    (default) AND `Tree` non-null AND Tree.IsDisposed=false. Reuse the root tree we
        //    already built around _skeletonRootScope.
        var disposedField = navTenantType.GetField("disposed", BindingFlags.NonPublic | BindingFlags.Instance);
        if (disposedField != null) FieldPoke.SetInstance(disposedField, _skeletonSystemTenant, false);
        var treeBackingField = navTenantType.GetField("<Tree>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (treeBackingField != null && _skeletonRootScope != null)
        {
            // _skeletonRootScope.Tree is a TreeHandler with hostObject != null → IsDisposed==false.
            var rootScopeTree = _skeletonRootScope.GetType()
                .GetProperty("Tree", BindingFlags.Public | BindingFlags.Instance)?
                .GetValue(_skeletonRootScope);
            if (rootScopeTree != null)
                FieldPoke.SetInstance(treeBackingField, _skeletonSystemTenant, rootScopeTree);
        }

        // 4. Wire skeleton NCLMetadata into the skeleton SystemTenant's `nclMetadata` field.
        var stNclField = systemTenantType.GetField("nclMetadata", BindingFlags.NonPublic | BindingFlags.Instance);
        if (stNclField != null)
            FieldPoke.SetInstance(stNclField, _skeletonSystemTenant, _skeletonNCLMetadata);

        // 4½. Seed the skeleton SystemTenant's `metadataProvider` field with a real
        //      MetadataProvider — exactly what NavSystemTenant's own ctor does
        //      (`metadataProvider = new MetadataProvider();`). We skipped that ctor via
        //      GetUninitializedObject, so NavGlobal.MetadataProvider (=> SystemTenant.MetadataProvider)
        //      was null. Virtual-table data providers (FieldDataProvider, AllObjDataProvider, …)
        //      derive from MetadataDataProvider whose ctor `ArgumentNullException.ThrowIfNull`s the
        //      MetadataProvider, so without this the virtual Field table (2000000041) could not be
        //      served and BC's Field-iterating code threw "There is no Field within the filter."
        //      A bare `new MetadataProvider()` is the faithful default — the FieldDataProvider's
        //      GetFieldsOnTable path reads only NclMetadata, never dereferencing this provider.
        // Seeding a real MetadataProvider lets us construct a MANAGED FieldDataProvider whose
        // row-builder (GetFieldRecordBuffer) materialises the virtual Field table (2000000041)
        // rows — see RecordPatches.FieldVirtualTable.cs. DEFAULT-ON: the downstream Field.FindSet()
        // is now routed through a managed find interception (RecordPatches.FieldFindIntercept.cs)
        // that bypasses BC's R2R DataAccess.InnerFindAsync (which AVs on this virtual system
        // table), so the whole virtual-Field path ships on by default.
        // NOTE: seeding NavSystemTenant.metadataProvider is deferred to a LAZY call
        // (EnsureMetadataProviderSeeded), triggered the first time the virtual Field table
        // (2000000041) is actually accessed — see RecordPatches.FieldVirtualTable. Seeding it
        // eagerly here changed NavGlobal.MetadataProvider for ALL tests and was observed to
        // perturb unrelated paths (e.g. NavQuery.ValidateTablesNotVirtual). Lazy seeding keeps
        // every non-Field-table test byte-identical to baseline.
        _systemTenantTypeForSeed = systemTenantType;

        // 4a. Seed NavTenant.defaultEncoding so NavTenant.DefaultEncoding returns without touching the
        //      tenant Database. With a non-null tenant now wired onto the session (step below), the
        //      blob/stream text path (NCLManagedAdapter.GetDefaultEncoding → session.Tenant.DefaultEncoding)
        //      reaches NavTenant.DefaultEncoding; its getter falls into `IsDatabaseInitialized`
        //      (→ database.IsValueCreated) when defaultEncoding is null, which NREs because the
        //      skeleton tenant's `database` LazyEx is null. Pre-setting defaultEncoding short-circuits
        //      that branch.
        //
        //      The VALUE must be a SINGLE-BYTE code page, not UTF-8. This field is what
        //      TextEncoding::Windows and ::MSDos resolve to — BC seeds it from
        //      NCLManagedAdapter.SystemCodePageEncoding, i.e. Encoding.GetEncoding(0), the
        //      host's ANSI code page, which on every Windows service tier BC ships on is
        //      cp1252. On Linux that same call returns UTF-8, so taking BC's expression
        //      literally here would be faithful to the CALL and wrong about the ANSWER.
        //
        //      The difference is not cosmetic: AL that assembles a binary format byte by
        //      byte — a PDF content stream, a fixed-width export, anything computing its own
        //      offsets — depends on one character costing one byte, and UTF-8 silently makes
        //      an em dash three. Every offset derived from the text is then wrong, far from
        //      where the encoding was chosen. cp1252 is also what /WinAnsiEncoding means, so
        //      it is the only answer consistent with the rest of the platform.
        var defaultEncodingField = navTenantType.GetField("defaultEncoding", BindingFlags.NonPublic | BindingFlags.Instance);
        if (defaultEncodingField != null)
            FieldPoke.SetInstance(defaultEncodingField, _skeletonSystemTenant, AnsiCodePageEncoding());
        else
            Console.Error.WriteLine("[BcRuntime] InjectSkeletonSystemTenant: NavTenant.defaultEncoding field NOT FOUND");

        // 4b. Populate the skeleton tenant's TenantSettings and wire the tenant onto the session.
        //
        //     "Environment Information" (CU457) → "Environment Information Impl." (CU3702) → its
        //     navTenantSettingsHelper.IsSandbox()/IsProduction()/GetEnvironmentName() call into
        //     Microsoft.Dynamics.Nav.NavUserAccount.NavTenantSettingsHelper, whose bodies read
        //     `NavCurrentThread.Session.Tenant.TenantSettings.EnvironmentType` (and EnvironmentName).
        //     On the headless skeleton `NavSession.tenant`/`systemTenant` were null, so the very
        //     first hop (Session.Tenant) NRE'd inside the .NET helper and surfaced as
        //     NavNCLDotNetInvokeException(IsSandbox).
        //
        //     NavTenantSettings (Microsoft.Dynamics.Nav.Types) is a thin wrapper: every property is
        //     backed by an inner `Dictionary<string,object> tenantSettings`, read via
        //     `Get(default, name)` which returns the *default* when the key is absent. So an empty
        //     dictionary yields EnvironmentType=Production (the literal default in the getter,
        //     matching the value the real NavSystemTenant ctor passes) and EnvironmentName=null.
        //     A GetUninitializedObject instance leaves that dictionary null → TryGetValue NREs;
        //     initialising it to an empty dictionary is all that is required.
        //
        //     Faithful headless value at the TENANT level: the runner runs no service tier and no
        //     Azure tenant — the equivalent of an OnPrem deployment, so the EMPTY dictionary default
        //     (EnvironmentType=Production, EnvironmentName=null) is the correct tenant-settings
        //     answer. But IsSandbox()/IsProduction() do NOT read tenant settings directly — they
        //     read them only as a FALLBACK when `Session.TestExecution.InTest` is false (see step 4c
        //     below, which wires the same "test execution is always a sandbox" seam BC's own
        //     service-tier test harness uses). isSaaSConfig has no analogous test-harness seam and
        //     stays false, so IsSaaS() (=IsSandbox() && isSaaSConfig) remains false even once 4c
        //     flips IsSandbox() to true.
        var tenantSettingsField   = navTenantType.GetField("tenantSettings", BindingFlags.NonPublic | BindingFlags.Instance);
        var navTenantSettingsType = tenantSettingsField?.FieldType;
        if (navTenantSettingsType != null && tenantSettingsField != null)
        {
            var skeletonTenantSettings = RuntimeHelpers.GetUninitializedObject(navTenantSettingsType);

            // Initialise the inner property dictionary that all NavTenantSettings getters read
            // through (mirrors the type's own field initialiser `= new Dictionary<string,object>()`).
            var innerDictField = navTenantSettingsType.GetField("tenantSettings",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (innerDictField != null)
            {
                var dict = Activator.CreateInstance(innerDictField.FieldType)!;
                FieldPoke.SetInstance(innerDictField, skeletonTenantSettings, dict);
            }
            else
                Console.Error.WriteLine("[BcRuntime] InjectSkeletonSystemTenant: NavTenantSettings.tenantSettings dict field NOT FOUND");

            FieldPoke.SetInstance(tenantSettingsField, _skeletonSystemTenant!, skeletonTenantSettings);
            Console.Error.WriteLine("[BcRuntime] InjectSkeletonSystemTenant: skeleton TenantSettings (default Production env) wired");

            // NavTenant.Id => id ?? tenantSettings.Id — both null on the skeleton.
            // NavFile.GetUserDirectoryAndEnsureExists (report temp-file path) does
            // Path.Combine(serverUsersFolder, Tenant.Id, …) → ArgumentNull without it.
            FieldInfo? tenantIdField = null;
            for (var walkT = _skeletonSystemTenant!.GetType(); walkT != null && tenantIdField == null; walkT = walkT.BaseType)
                tenantIdField = walkT.GetField("id", BindingFlags.NonPublic | BindingFlags.Instance);
            if (tenantIdField != null && tenantIdField.GetValue(_skeletonSystemTenant) == null)
                FieldPoke.SetInstance(tenantIdField, _skeletonSystemTenant, "default");
        }
        else
        {
            Console.Error.WriteLine("[BcRuntime] InjectSkeletonSystemTenant: NavTenant.tenantSettings field NOT FOUND — IsSandbox/IsSaaS will still NRE");
        }

        // Wire the skeleton tenant onto the session's `tenant` AND `systemTenant` fields so
        // `Session.Tenant` and `Session.SystemTenant` resolve to the skeleton (carrying its
        // TenantSettings, default encoding and MetadataProvider) instead of null.
        //
        // `systemTenant` used to be left alone here on the reasoning that the NavTenantCollection
        // injection (step 6 below) already covers the STATIC NavGlobal.SystemTenant. That was true
        // only for code reaching metadata statically. Every INSTANCE path goes through the session:
        // NavSession.MetadataProvider is literally `SystemTenant.MetadataProvider`, so a null
        // session.systemTenant NREs before any of our metadata dispatch is reached. The live
        // example is NavXmlPort.BeginInitialization —
        // `base.Tree.Session.MetadataProvider.GetXmlPortMetadata(NclMetaXmlPort)` — which every
        // real xmlport runs from its generated ctor.
        //
        // That NRE was a pure TEST-ORDER dependency: NavReportSync.SeedSessionSystemTenant seeds
        // the same field as a side effect of constructing a report, so xmlport tests passed in any
        // bundle where a report test happened to run first, and failed (14 corpus tests, repro
        // `--test Codeunit6020`) in one where none did. Seeding it here, once, for every session
        // makes the field's value independent of what ran before — which is also what real BC has,
        // since NavSession is never constructed without a system tenant.
        if (_skeletonSession != null)
        {
            var sessType = _skeletonSession.GetType();
            var sessTenantField = sessType.GetField("tenant", BindingFlags.NonPublic | BindingFlags.Instance);
            if (sessTenantField != null && sessTenantField.GetValue(_skeletonSession) == null)
            {
                FieldPoke.SetInstance(sessTenantField, _skeletonSession, _skeletonSystemTenant!);
                Console.Error.WriteLine("[BcRuntime] InjectSkeletonSystemTenant: skeleton tenant wired onto session.tenant");
            }

            FieldInfo? sessSystemTenantField = null;
            for (var walkT = sessType; walkT != null && sessSystemTenantField == null; walkT = walkT.BaseType)
                sessSystemTenantField = walkT.GetField("systemTenant", BindingFlags.NonPublic | BindingFlags.Instance);
            if (sessSystemTenantField == null)
                Console.Error.WriteLine(
                    "[BcRuntime] InjectSkeletonSystemTenant: NavSession.systemTenant field NOT FOUND — "
                    + "Session.MetadataProvider will NRE on every instance metadata path");
            else if (sessSystemTenantField.GetValue(_skeletonSession) == null)
            {
                FieldPoke.SetInstance(sessSystemTenantField, _skeletonSession, _skeletonSystemTenant!);
                Console.Error.WriteLine("[BcRuntime] InjectSkeletonSystemTenant: skeleton tenant wired onto session.systemTenant");
            }

            // Session.UserFolder — auto-property, null on the skeleton (the real
            // service tier resolves it from the user's server folder). NavFile.TempPath
            // does Path.Combine(Session.UserFolder, "TEMP\\") → ArgumentNull without it,
            // which surfaces as NavNCLInvalidPathException and traps SaveAs to `false`.
            // The report XML/dataset processors buffer through NavFile temp files, so
            // wire a real writable runner-owned folder here.
            var userFolderBacking = sessType.GetField("<UserFolder>k__BackingField",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (userFolderBacking != null && string.IsNullOrEmpty(userFolderBacking.GetValue(_skeletonSession) as string))
            {
                var userFolder = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "al-runner-navserver", "user", Environment.ProcessId.ToString());
                System.IO.Directory.CreateDirectory(userFolder);
                FieldPoke.SetInstance(userFolderBacking, _skeletonSession, userFolder);
                Console.Error.WriteLine($"[BcRuntime] InjectSkeletonSystemTenant: session.UserFolder wired to {userFolder}");
            }

            // Session.Diagnostics — auto-property, null on the skeleton. The report
            // execution chain calls session.Diagnostics.CreateTraceScope(...) directly
            // (RunReportInternalCoreAsync / ExecuteDataItemIteratorAsync). Wire the same
            // instance BC uses as its ambient fallback (NavDiagnostics.GetMostSpecificInstance)
            // so trace scopes are real, not fabricated.
            var diagBacking = sessType.GetField("<Diagnostics>k__BackingField",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (diagBacking != null && diagBacking.GetValue(_skeletonSession) == null)
            {
                // NavDiagnostics lives in Types.dll (namespace Microsoft.Dynamics.Nav.Diagnostic).
                var tDiag = typeof(Microsoft.Dynamics.Nav.Runtime.NavSession).Assembly
                    .GetType("Microsoft.Dynamics.Nav.Diagnostic.NavDiagnostics")
                    ?? AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => a.GetType("Microsoft.Dynamics.Nav.Diagnostic.NavDiagnostics"))
                        .FirstOrDefault(t => t != null);
                var ambient = tDiag?.GetProperty("GetMostSpecificInstance",
                        BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                    ?? tDiag?.GetField("GetMostSpecificInstance",
                        BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (ambient != null)
                {
                    FieldPoke.SetInstance(diagBacking, _skeletonSession, ambient);
                    Console.Error.WriteLine("[BcRuntime] InjectSkeletonSystemTenant: session.Diagnostics wired to ambient NavDiagnostics");
                }
                else
                    Console.Error.WriteLine("[BcRuntime] InjectSkeletonSystemTenant: NavDiagnostics.GetMostSpecificInstance NOT resolved — session.Diagnostics stays null");
            }
        }

        // 4c. Resolve (once) the reflection handles TestExecutor needs to wire the "a running
        //     test is always a sandbox, never production" seam BC's own real service-tier test
        //     harness relies on. See EnterTestExecutionScope/LeaveTestExecutionScope below for
        //     the per-test call, and the rationale.
        if (_skeletonSession != null)
        {
            try
            {
                var sessType = _skeletonSession.GetType();
                _piTestExecution = sessType.GetProperty("TestExecution",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var testExecution = _piTestExecution?.GetValue(_skeletonSession);
                if (testExecution == null)
                {
                    Console.Error.WriteLine("[BcRuntime] InjectSkeletonSystemTenant: session.TestExecution returned null — sandbox seam NOT wired, IsSandbox() stays Production-default");
                }
                else
                {
                    _testExecutionInstance = testExecution;

                    // This NavTestExecution's own TreeHandler captured `session` at
                    // construction from its parent handler, which was the session root — and
                    // that root is built by CreateTreeRoot with parent == null, the one ctor
                    // path that never assigns `session`. So Tree.Session was null here even
                    // after the root itself was fixed, and NavTestExecution's modal-page
                    // dispatch (`new NavScope(base.Tree.Session)`) threw
                    // ArgumentNullException(parent) before any handler could run.
                    var treeProp = testExecution.GetType().GetProperty("Tree",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var handler = treeProp?.GetValue(testExecution);
                    // Walk the hierarchy: `session` is a private field of the TreeHandler
                    // BASE class, and GetField on the concrete TreeObjectHandler does not
                    // return private members of base types — which is why an earlier version
                    // of this poke silently did nothing at all.
                    FieldInfo? fHandlerSession = null;
                    for (var wt = handler?.GetType(); wt != null && fHandlerSession == null; wt = wt.BaseType)
                        fHandlerSession = wt.GetField("session", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fHandlerSession != null && fHandlerSession.GetValue(handler) == null)
                        FieldPoke.SetInstance(fHandlerSession, handler!, _skeletonSession);

                    // The test client session BC would have Assembly.Load-ed from the
                    // TestPageClient. Its GetPage is what hands the AL [ModalPageHandler] the
                    // page BC just opened; left null, BC's own pushed dialog delegate NREs the
                    // instant a modal page is dispatched. See RunnerTestClientSession.
                    var fTestClientSession = testExecution.GetType()
                        .GetField("testClientSession", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fTestClientSession != null)
                        FieldPoke.SetInstance(fTestClientSession, testExecution,
                            new AlRunner.Patches.RunnerTestClientSession(_skeletonSession!));
                    else
                        Console.Error.WriteLine(
                            "[BcRuntime] NavTestExecution.testClientSession NOT FOUND — modal-page "
                            + "handler dispatch will fail");

                    var testExecType = testExecution.GetType(); // Microsoft.Dynamics.Nav.Runtime.NavTestExecution
                    _fExecutingTestCodeUnit = testExecType.GetField("executingTestCodeUnit", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (_fExecutingTestCodeUnit == null)
                        Console.Error.WriteLine("[BcRuntime] InjectSkeletonSystemTenant: NavTestExecution.executingTestCodeUnit field NOT FOUND — sandbox seam NOT wired");

                    var nuaAsm = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.NavUserAccount")
                        ?? Assembly.Load("Microsoft.Dynamics.Nav.NavUserAccount");
                    var tenantSettingsHelperType = nuaAsm?.GetType("Microsoft.Dynamics.Nav.NavUserAccount.NavTenantSettingsHelper");
                    _mSetTestTenantEnvironmentType = tenantSettingsHelperType?.GetMethod("SetTestTenantEnvironmentType",
                        BindingFlags.Public | BindingFlags.Static);
                    if (_mSetTestTenantEnvironmentType == null)
                        Console.Error.WriteLine("[BcRuntime] InjectSkeletonSystemTenant: NavTenantSettingsHelper.SetTestTenantEnvironmentType NOT FOUND — sandbox seam NOT wired, IsSandbox() stays Production-default");
                }
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                Console.Error.WriteLine($"[BcRuntime] InjectSkeletonSystemTenant: sandbox seam resolution failed ({inner.GetType().Name}: {inner.Message}) — IsSandbox() stays Production-default");
            }
        }

        // 5. NCLMetaApplicationObject.Populate and .CompileAndLoadClrObject are Cecil-owned
        //    (see NclCecilRewrite.cs).

        // 6. Inject skeleton SystemTenant into the real Tenants collection.
        var tenantsProp = envType.GetProperty("Tenants",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var instance = envType.GetField("instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        var tenants = (instance != null && tenantsProp != null) ? tenantsProp.GetValue(instance) : null;
        if (tenants != null)
        {
            var tenantsType = tenants.GetType();
            var stField = tenantsType.GetField("systemTenant", BindingFlags.NonPublic | BindingFlags.Instance);
            if (stField != null)
            {
                FieldPoke.SetInstance(stField, tenants, _skeletonSystemTenant);
                Console.Error.WriteLine("[BcRuntime] InjectSkeletonSystemTenant: skeleton wired into Tenants.systemTenant");
            }
            else
                Console.Error.WriteLine("[BcRuntime] InjectSkeletonSystemTenant: systemTenant field NOT FOUND on " + tenantsType.FullName);
        }
        else
        {
            Console.Error.WriteLine("[BcRuntime] InjectSkeletonSystemTenant: Tenants is null — env ctor likely fell back to skeleton");
        }
    }

    /// <summary>
    /// The tenant's default encoding: the ANSI code page a BC service tier runs on.
    ///
    /// .NET Core carries no code pages beyond the Unicode family until
    /// CodePagesEncodingProvider is registered — which is exactly what BC's own
    /// NCLManagedAdapter static constructor does before it asks for Encoding.GetEncoding(0).
    /// Registering it again is harmless (the call is idempotent) and removes the dependency
    /// on that type having been touched first.
    ///
    /// Falls back to Latin-1 rather than UTF-8 if 1252 is somehow unavailable: the property
    /// that matters to every caller is SINGLE-BYTE, and Latin-1 keeps that invariant while
    /// UTF-8 breaks it. A wrong glyph in the 0x80..0x9F band is a much smaller lie than a
    /// character that silently costs three bytes.
    /// </summary>
    private static System.Text.Encoding AnsiCodePageEncoding()
    {
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return System.Text.Encoding.GetEncoding(1252);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[BcRuntime] cp1252 unavailable ({ex.GetType().Name}); TextEncoding::Windows "
                + "falls back to Latin-1. Characters in the cp1252 0x80-0x9F block (em dash, "
                + "euro sign, curly quotes) will not round-trip.");
            return System.Text.Encoding.Latin1;
        }
    }
}
