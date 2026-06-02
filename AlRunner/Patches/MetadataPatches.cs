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
using AlRunnerV2.Infrastructure;
using JmpHook = AlRunnerV2.Infrastructure.JmpHook;

namespace AlRunnerV2;

public static partial class BcRuntime
{
    private static object? _skeletonNCLMetadata;
    private static object? _skeletonSystemTenant;
    private static Type? _systemTenantTypeForSeed;
    private static bool _metadataProviderSeeded;

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
        //      that branch. UTF-8 is BC's own default tenant encoding (TextEncoding.UTF8) — faithful.
        var defaultEncodingField = navTenantType.GetField("defaultEncoding", BindingFlags.NonPublic | BindingFlags.Instance);
        if (defaultEncodingField != null)
            FieldPoke.SetInstance(defaultEncodingField, _skeletonSystemTenant, System.Text.Encoding.UTF8);
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
        //     Faithful headless value: the runner runs no service tier and no Azure tenant — the
        //     equivalent of an OnPrem deployment. The default Production environment is exactly the
        //     non-sandbox, non-SaaS case: IsSandbox()=false, IsProduction()=true, and IsSaaS() (whose
        //     isSaaSConfig stays false) =false. These are the correct OnPrem answers, not a fake.
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
        }
        else
        {
            Console.Error.WriteLine("[BcRuntime] InjectSkeletonSystemTenant: NavTenant.tenantSettings field NOT FOUND — IsSandbox/IsSaaS will still NRE");
        }

        // Wire the skeleton tenant onto the session's `tenant` field so `Session.Tenant` resolves to
        // the skeleton (carrying its TenantSettings + default encoding) instead of null. We only set
        // `tenant` — the existing `systemTenant` injection into the NavTenantCollection (step 6 below)
        // already covers NavGlobal.SystemTenant; touching `Session.SystemTenant` is unnecessary and
        // out of scope here.
        if (_skeletonSession != null)
        {
            var sessType = _skeletonSession.GetType();
            var sessTenantField = sessType.GetField("tenant", BindingFlags.NonPublic | BindingFlags.Instance);
            if (sessTenantField != null && sessTenantField.GetValue(_skeletonSession) == null)
            {
                FieldPoke.SetInstance(sessTenantField, _skeletonSession, _skeletonSystemTenant!);
                Console.Error.WriteLine("[BcRuntime] InjectSkeletonSystemTenant: skeleton tenant wired onto session.tenant");
            }
        }

        // 5. Populate cache hook target — NCLMetaApplicationObject.Populate is called
        //    from NCLMetadata.GetMetaApplicationObjectInternal when the cache entry's
        //    `metadataLoaded` flag is false. Our hand-built NCLMetaTable instances have
        //    no NCLObjectXmlMetadataLoader / MetaObjectCache backing, so the original
        //    Populate would NRE inside LoadTableMetadata. Replace it with a no-op:
        //    the cache populator already FieldPokes everything we need (fields, keys).
        //    Field-poking metadataLoaded=true alone is not enough — JIT inlines the
        //    MetadataLoaded getter and the runtime sometimes still drops into Populate
        //    along the lock-retry path; hooking the method body short-circuits that.
        var nclAppObjType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaApplicationObject");
        if (nclAppObjType != null)
        {
            var populate = nclAppObjType.GetMethod("Populate",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
            if (populate != null)
            {
                JmpHook.Apply(populate,
                    typeof(BcRuntime).GetMethod(nameof(BcRuntime.NoOp_OneArg),
                        BindingFlags.Public | BindingFlags.Static)!,
                    "NCLMetaApplicationObject.Populate");
                Console.Error.WriteLine("[BcRuntime] NCLMetaApplicationObject.Populate hooked → NoOp");
            }

            // CompileAndLoadClrObject — same story as Populate. Original calls
            // `nclMetaObjectCLRTypeContainer.ApplicationObjectClrType = LoadClrType();`
            // which NREs (container is null on hand-built metas; LoadClrType walks
            // ObjectLoader which is null). The downstream property getter
            // ApplicationObjectClrType is already JMP-hooked
            // (NCLMetaApplicationObject_get_ApplicationObjectClrType) to look up
            // Record{ID} from the loaded test assembly directly, so making this a
            // no-op is safe.
            var compileLoad = nclAppObjType.GetMethod("CompileAndLoadClrObject",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
            if (compileLoad != null)
            {
                JmpHook.Apply(compileLoad,
                    typeof(BcRuntime).GetMethod(nameof(BcRuntime.NoOp_OneArg),
                        BindingFlags.Public | BindingFlags.Static)!,
                    "NCLMetaApplicationObject.CompileAndLoadClrObject");
                Console.Error.WriteLine("[BcRuntime] NCLMetaApplicationObject.CompileAndLoadClrObject hooked → NoOp");
            }
        }

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
}
