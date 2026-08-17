// BcRuntime — applies Linux-compatibility patches to BC service-tier DLLs at process start.
// Lifted directly from spike/bc-abi-identity/runner/LinuxBootstrap.cs (proven to work end-to-end).
// Pattern: bc-linux's JMP-hook via mprotect + RuntimeHelpers.PrepareMethod.
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AlRunner.Infrastructure;

namespace AlRunner;

public static partial class BcRuntime
{
    private static bool _applied;
    private static Type? _navEnvironmentType;
    private static object? _skeletonSession;

    /// <summary>The skeleton NavSession every runner-side patch reaches BC through.</summary>
    internal static object? SkeletonSession => _skeletonSession;
    private static Microsoft.Dynamics.Nav.Runtime.NavMethodScope? _skeletonRootScope;
    public static Microsoft.Dynamics.Nav.Runtime.ITreeObject? RootTreeStub;

    public static volatile bool OosHooksActive;

    // Reflected fields used by the NavMethodScope ctor replacement.
    // Populated in ApplyAllPatches; used in NavMethodScopeCtorReplacement.
    private static FieldInfo? _fTreeObjTree;           // TreeObject.tree
    private static FieldInfo? _fMsSession;             // NavMethodScope.session
    private static FieldInfo? _fMsParentScope;         // NavMethodScope.parentScope
    private static FieldInfo? _fMsFlags;               // NavMethodScope.flags
    private static FieldInfo? _fMsStackDepth;          // NavMethodScope.<StackDepth>k__BackingField
    private static FieldInfo? _fMsTopLevelAppObj;      // NavMethodScope.<TopLevelApplicationObject>k__BackingField
    private static FieldInfo? _fSessCurrentScope;      // NavSession.<CurrentMethodScope>k__BackingField
    private static MethodInfo? _mCreateTreeHandler;    // TreeHandler.CreateTreeHandler
    private static Type? _navNCLDialogExceptionType;   // NavNCLDialogException (for NavDialog.ALError replacement)

    // MEMORY LEAK FIX fields (see MethodScopePatches.NavMethodScope_Dispose) — private
    // doubly-linked-list fields declared on the ABSTRACT TreeHandler base class. Must be
    // resolved via typeof(TreeHandler)/treeHandlerType directly: GetField(NonPublic|Instance)
    // on a concrete subtype's Type does NOT surface inherited private base-class fields.
    private static FieldInfo? _fTreeHandlerParent;          // TreeHandler.parentHandler
    private static FieldInfo? _fTreeHandlerFirstChildBase;  // TreeHandler.firstChildHandler
    private static FieldInfo? _fTreeHandlerPrevSibling;     // TreeHandler.previousSiblingHandler
    private static FieldInfo? _fTreeHandlerNextSiblingBase; // TreeHandler.nextSiblingHandler

    // NavApplicationObjectBase ctor replacement fields.
    private static FieldInfo? _fAoSession;             // NavApplicationObjectBase.session
    private static FieldInfo? _fAoObjectId;            // NavApplicationObjectBase.objectId (readonly struct)
    private static FieldInfo? _fAoOrigGroupId;         // NavApplicationObjectBase.originalAppGroupId
    private static FieldInfo? _fAoRuntimeGroupId;      // NavApplicationObjectBase.runtimeAppGroupId
    private static FieldInfo? _fNavComplexValueTree;   // NavComplexValue.tree (distinct from TreeObject.tree)
    internal static FieldInfo? _fTreeHandlerSession;   // TreeHandler.session (private readonly, on base class)
    private static object? _skeletonCompany;            // cached skeleton NavCompany (CompanyNameToken=0)

    // NavRecord write-path replacement fields (cached for perf).
    private static object? _skeletonNavServerEventSource;
    private static FieldInfo? _fNavRecordRecordImplementation;     // NavRecord.recordImplementation
    private static MethodInfo? _mRecordImplementationInsertRecordAsync;  // RecordImplementation.InsertRecordAsync
    private static MethodInfo? _mRecordImplementationModifyRecordAsync;  // RecordImplementation.ModifyRecordAsync
    private static MethodInfo? _mRecordImplementationDeleteRecordAsync;  // RecordImplementation.DeleteRecordAsync
    private static MethodInfo? _mRecordImplementationRenameRecordAsync;  // RecordImplementation.RenameRecordAsync
    private static MethodInfo? _mNavRecordCloneRecord;                   // NavRecord.CloneRecord(ITreeObject,bool,bool)
    private static MethodInfo? _mNavRecordALInsertAsync3;  // NavRecord.ALInsertAsync(DataError,bool,bool) — hooked for AI
    private static MethodInfo? _mNavRecordALInit;           // NavRecord.ALInit() — hooked for PK reset
    private static MethodInfo? _mNavTextBuilderALInsert;    // NavTextBuilder.ALInsert(DataError,int,string)
    // AutoIncrement: tableId → AI fieldNo; tableId → last assigned counter value
    private static readonly ConcurrentDictionary<int, int>  _aiFieldIds  = new();
    private static readonly ConcurrentDictionary<int, long> _aiCounters  = new();

    internal static IReadOnlyDictionary<int, long> CaptureAutoIncrementBaseline()
        => new Dictionary<int, long>(_aiCounters);

    internal static void RestoreAutoIncrementBaseline(IReadOnlyDictionary<int, long>? baseline)
    {
        _aiCounters.Clear();
        if (baseline == null) return;
        foreach (var pair in baseline)
            _aiCounters[pair.Key] = pair.Value;
    }
    private static FieldInfo? _fRecordImplementationDataAccess;          // RecordImplementation.dataAccess
    private static FieldInfo? _fRecordImplementationMetaTable;          // RecordImplementation.metaTable
    private static FieldInfo? _fRecordImplementationMutableRecordBuffer; // RecordImplementation.mutableRecordBuffer
    private static MethodInfo? _mDataAccessTryGetByPrimaryKeyAsync;
    private static PropertyInfo? _pMrbResultResult;     // MutableRecordBufferResult<bool>.Result
    private static PropertyInfo? _pMrbResultRecordBuffer;

    // Current bundle app info for the NavApp.GetCurrentModuleInfo polyfill shim.
    private static (Guid AppId, string Name, string Publisher, string Version) _currentBundleInfo
        = (Guid.Empty, "Unknown", "Unknown", "1.0.0.0");

    public static void SetCurrentBundleInfo(Guid appId, string name, string publisher, string version)
        => _currentBundleInfo = (appId, name, publisher, version);

    // Per-assembly module identity: every emitted AL assembly (test bundle emit AND
    // each dependency emit) is a distinct BC "module". NavApp.GetCurrentModuleInfo
    // inside a dependency's code (e.g. SPBLIC's CheckSupportedVersion) must see THAT
    // app's name/version, not the bundle's — real BC resolves the module of the
    // currently executing object. Keyed by Assembly instance (Assembly.Load(byte[])).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        Assembly, (Guid AppId, string Name, string Publisher, string Version)> _moduleInfoByAssembly = new();

    public static void RegisterModuleInfoForAssembly(
        Assembly asm, Guid appId, string name, string publisher, string version)
        => _moduleInfoByAssembly[asm] = (appId, name, publisher, version);

    /// <summary>
    /// Register an assembly with the current bundle's app info so AlCallStackCapture
    /// can decorate AL call stack frames from that assembly.
    /// </summary>
    public static void RegisterTestAssemblyInfo(System.Reflection.Assembly asm)
    {
        var (appId, name, publisher, version) = _currentBundleInfo;
        AlRunner.Infrastructure.AlCallStackCapture.RegisterAssemblyInfo(asm, name, publisher, version);
        _moduleInfoByAssembly[asm] = (appId, name, publisher, version);
    }

    public static (Guid AppId, string Name, string Publisher, string Version) GetCurrentModuleAppInfo()
        => _currentBundleInfo;

    /// <summary>
    /// Module info of the app whose emitted assembly is <paramref name="asm"/> —
    /// called by the per-assembly ALNavApp_GetCurrentModuleInfo polyfill with its own
    /// executing assembly. Falls back to the current bundle info (single-bundle case /
    /// pre-registration edge).
    /// </summary>
    public static (Guid AppId, string Name, string Publisher, string Version) GetModuleAppInfoFor(Assembly asm)
        => _moduleInfoByAssembly.TryGetValue(asm, out var info) ? info : _currentBundleInfo;

    /// <summary>
    /// Stack-walk version of <see cref="GetModuleAppInfoFor"/> for use from the Cecil
    /// patch on <c>ALNavApp.ALGetCurrentModuleInfo</c> in precompiled deps (where
    /// <c>Assembly.GetExecutingAssembly()</c> would return the Ncl.dll or runner assembly,
    /// not the dep's assembly). Walks the call stack and returns the info for the FIRST
    /// registered AL assembly found — that is the precompiled dep whose AL code called
    /// NavApp.GetCurrentModuleInfo.
    /// </summary>
    public static (Guid AppId, string Name, string Publisher, string Version) GetCurrentModuleFromCallStack()
    {
        try
        {
            var trace = new System.Diagnostics.StackTrace(fNeedFileInfo: false);
            for (int i = 0; i < trace.FrameCount; i++)
            {
                var asm = trace.GetFrame(i)?.GetMethod()?.DeclaringType?.Assembly;
                if (asm == null) continue;
                if (_moduleInfoByAssembly.TryGetValue(asm, out var info)) return info;
            }
        }
        catch { }
        return _currentBundleInfo;
    }

    /// <summary>
    /// Stack-walk version of <see cref="GetCallerModuleAppInfoFor"/> for use from the
    /// Cecil patch on <c>ALNavApp.ALGetCallerModuleInfo</c> in precompiled deps.
    /// Prefers the faithful method-scope walk; the assembly stack-walk is the fallback
    /// for when the scope chain is not populated.
    /// </summary>
    public static (Guid AppId, string Name, string Publisher, string Version) GetCallerModuleFromCallStack()
    {
        var immediate = TryGetImmediateCallerModule();
        if (immediate != null) return immediate.Value;

        try
        {
            var trace = new System.Diagnostics.StackTrace(fNeedFileInfo: false);
            Assembly? selfAsm = null;
            for (int i = 0; i < trace.FrameCount; i++)
            {
                var asm = trace.GetFrame(i)?.GetMethod()?.DeclaringType?.Assembly;
                if (asm == null) continue;
                if (!_moduleInfoByAssembly.ContainsKey(asm)) continue;
                if (selfAsm == null) { selfAsm = asm; continue; }
                if (asm != selfAsm) return _moduleInfoByAssembly[asm];
            }
            if (selfAsm != null) return GetModuleAppInfoFor(selfAsm);
        }
        catch { }
        return _currentBundleInfo;
    }

    /// <summary>
    /// <c>NavApp.GetCallerModuleInfo</c>, faithful to BC's own rule.
    ///
    /// BC's <c>ALGetCallerModuleInfo</c> calls
    /// <c>GetCallingAppId(Guid.Empty, excludeCurrentMethod: true)</c>, which walks the
    /// method-scope chain, skips EXACTLY ONE scope, then breaks on the very next stack
    /// frame. So the answer is the module of the IMMEDIATE caller — BC never walks past
    /// frames that happen to belong to the same app.
    ///
    /// The runner used to answer "the nearest frame from a DIFFERENT registered
    /// assembly". That differs precisely when an app calls into itself through another of
    /// its own objects: BC says "this app", the runner said "whoever called this app".
    /// Measured consequence — an ISV registry keyed on <c>GetCallerModuleInfo().Id()</c>
    /// wrote one asset row per calling app instead of one row for itself, so a later name
    /// lookup found two owners, reported the name AMBIGUOUS, and surfaced as a
    /// font-variant error nowhere near this call.
    ///
    /// BC's own scope chain cannot be used here: the runner leaves
    /// <c>session.CurrentMethodScope</c> at the RootMethodScope for source-compiled AL
    /// (measured), so the rule is applied to the managed stack instead. One AL method can
    /// occupy several managed frames — the AL emit adds a compiler-generated
    /// <c>&lt;Method&gt;_Scope__…</c> frame object nested in the same type — so those are
    /// folded away to keep "one frame per AL method invocation".
    ///
    /// Returns null when fewer than two AL frames are on the stack (a top-level AL entry
    /// such as an install trigger), leaving the existing fallback in place.
    /// </summary>
    private static (Guid AppId, string Name, string Publisher, string Version)? TryGetImmediateCallerModule()
    {
        try
        {
            var trace = new System.Diagnostics.StackTrace(fNeedFileInfo: false);
            Assembly? currentAlFrame = null;
            for (int i = 0; i < trace.FrameCount; i++)
            {
                var method = trace.GetFrame(i)?.GetMethod();
                var type = method?.DeclaringType;
                var asm = type?.Assembly;
                if (asm == null || !_moduleInfoByAssembly.ContainsKey(asm)) continue;
                // The polyfill shim is compiled INTO each AL assembly, so its frames pass
                // the assembly test above while being runner plumbing, not AL. Counting
                // them shifts the whole walk by one and yields the callee as its own caller.
                if (type!.Namespace != null
                    && type.Namespace.StartsWith("AlRunnerShim", StringComparison.Ordinal)) continue;
                // Same AL method, not a caller: fold away the emitted scope frame object.
                if (type.Name.Contains("_Scope", StringComparison.Ordinal)) continue;
                // #1722 — the CROSS-APP invocation path. Calling an AL procedure in another
                // app does not land as one managed frame: the AL emit routes it through a
                // compiler-generated `Codeunit<N>.OnInvoke` dispatcher, and that dispatcher
                // lives in the CALLEE's own assembly, one frame above the callee's real
                // method. It is the same AL method invocation, not a caller — exactly what
                // `_Scope` frames are folded for — but it is a plain method on the codeunit
                // type, so neither rule above catches it. Left unfolded it is accepted as
                // "the immediate caller" and the walk answers with the callee's own module,
                // making GetCallerModuleInfo indistinguishable from GetCurrentModuleInfo for
                // every library invoked across an app boundary. `OnInvoke` is emitted by the
                // AL compiler, never authored (AL's codeunit trigger is `OnRun`), so folding
                // it cannot swallow a genuine AL caller frame.
                if (method!.Name == "OnInvoke") continue;

                if (currentAlFrame == null) { currentAlFrame = asm; continue; }
                // BC breaks on the FIRST frame after the skipped one — even when it
                // belongs to the same app. Do not keep searching for a foreign one.
                return _moduleInfoByAssembly[asm];
            }
        }
        catch { /* fall back */ }
        return null;
    }

    /// <summary>Module info by AppId across every registered assembly (deps + bundle),
    /// for NavApp.GetModuleInfo(moduleId). Null when the id is unknown.</summary>
    public static (Guid AppId, string Name, string Publisher, string Version)? TryGetModuleInfoByAppId(Guid moduleId)
    {
        var (bid, bn, bp, bv) = _currentBundleInfo;
        if (moduleId == bid) return (bid, bn, bp, bv);
        foreach (var kv in _moduleInfoByAssembly)
            if (kv.Value.AppId == moduleId) return kv.Value;
        return null;
    }

    /// <summary>
    /// NavApp.GetCallerModuleInfo semantics: the module of the IMMEDIATE caller — see
    /// <see cref="TryGetCallerModuleFromMethodScopes"/> for BC's own rule and why
    /// "nearest DIFFERENT assembly" was wrong. The assembly stack-walk below remains as
    /// the fallback for when the method-scope chain is not populated; it still answers
    /// the nearest foreign module, which is correct whenever the immediate caller really
    /// is a different app (the common cross-module case).
    /// </summary>
    public static (Guid AppId, string Name, string Publisher, string Version) GetCallerModuleAppInfoFor(Assembly self)
    {
        var immediate = TryGetImmediateCallerModule();
        if (immediate != null) return immediate.Value;

        try
        {
            var trace = new System.Diagnostics.StackTrace(fNeedFileInfo: false);
            for (int i = 0; i < trace.FrameCount; i++)
            {
                var asm = trace.GetFrame(i)?.GetMethod()?.DeclaringType?.Assembly;
                if (asm == null || asm == self) continue;
                if (_moduleInfoByAssembly.TryGetValue(asm, out var info)) return info;
            }
        }
        catch { }
        return GetModuleAppInfoFor(self);
    }

    // Set to the currently-loaded test assembly so CreateTarget looks up codeunit types there.
    private static Assembly? _currentTestAssembly;

    /// <summary>
    /// The bundle assembly currently being executed, or null before the first
    /// <see cref="SetTestAssembly"/> / after <see cref="ResetForNewBundleReload"/>.
    /// AL-output type finders (Record/Codeunit/Page/…) prefer this so a server
    /// reload of the same-identity bundle resolves the freshly-emitted types
    /// rather than same-named types still loaded from the previous assembly
    /// (.NET cannot unload them).
    /// </summary>
    internal static Assembly? CurrentTestAssembly => _currentTestAssembly;

    /// <summary>
    /// For every bundle-emitted assembly's simple name, the assembly instance from the
    /// MOST RECENT compile of that app within this process — populated by
    /// <see cref="SetTestAssembly"/> for every app it loads, not only the one currently
    /// executing. .NET cannot unload assemblies, so a warm process that re-runs the same
    /// bundle SET more than once (server mode / <c>--watch</c>) leaves every earlier
    /// generation of EVERY app resident under its identical simple name (we re-emit under
    /// the same module name each cycle).
    ///
    /// This is what makes <see cref="IsStaleBundleAssembly"/> correct for a cross-app
    /// call: comparing only against <see cref="CurrentTestAssembly"/> (the old
    /// implementation) can identify a stale generation of the app that is CURRENTLY
    /// executing, but is blind to a stale generation of a SIBLING/dependency app that is
    /// not the one currently executing — e.g. app B's tests running while app A (a
    /// dependency B just called into) was edited and recompiled since the last cycle.
    /// That gap is issue #1901: a call from B's test code into A's just-edited codeunit
    /// resolved whichever generation of A's assembly <c>AppDomain.CurrentDomain
    /// .GetAssemblies()</c> happened to enumerate first — unspecified order, and in
    /// practice the OLDEST (first-loaded) generation — so the test kept passing against
    /// A's pre-edit behaviour.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Assembly> _latestGenerationByAssemblyName =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Records <paramref name="asm"/> as the current generation for its own simple name,
    /// superseding whatever this process previously registered under that name. Called
    /// from <see cref="SetTestAssembly"/> for every app it loads (own bundle AND every
    /// sibling in a multi-bundle invocation), so the registry stays accurate for apps
    /// that are not currently executing too — see <see cref="_latestGenerationByAssemblyName"/>.
    /// </summary>
    private static void RegisterAssemblyGeneration(Assembly asm)
    {
        var name = asm.GetName().Name;
        if (name != null) _latestGenerationByAssemblyName[name] = asm;
    }

    /// <summary>
    /// True when <paramref name="asm"/> is a previous-cycle generation of a bundle
    /// assembly that is still resident after a server/watch reload — same simple name as
    /// SOME app's current generation (we re-emit under the identical module name) but a
    /// different instance. AL-output scans that enumerate every loaded assembly (the
    /// event-subscriber registry, the Record/Codeunit/Page/… type finders) must skip
    /// these so a stale generation can never answer for an AL object name — whether that
    /// object still exists in the new generation or was deleted between cycles (the
    /// "tombstone" case from #1901: skipping every type in a stale assembly means a
    /// removed object can never be found there either).
    ///
    /// Backed by <see cref="_latestGenerationByAssemblyName"/> so this is accurate for
    /// EVERY app registered via <see cref="SetTestAssembly"/>, not only whichever one is
    /// <see cref="CurrentTestAssembly"/> right now (see that field's registration comment
    /// for why the old current-assembly-only check missed cross-app calls). Returns false
    /// for an assembly whose simple name was never registered (e.g. a genuine
    /// service-tier/dependency DLL, or normal one-shot mode with no reload).
    /// </summary>
    internal static bool IsStaleBundleAssembly(Assembly asm)
    {
        var name = asm.GetName().Name;
        return name != null
            && _latestGenerationByAssemblyName.TryGetValue(name, out var latest)
            && !ReferenceEquals(asm, latest);
    }

    /// <param name="wireFieldTriggers">
    /// Whether to run RecordPatches.WireFieldTriggerHandlersAll — a full walk of every
    /// table registered so far, not just this assembly's. Every caller that loads and
    /// runs exactly one assembly per invocation should leave this true (the default;
    /// matches the original single-call behaviour). A caller loading MULTIPLE
    /// assemblies before any of them runs (bundled mode: one module per app.json,
    /// see AppGroup) must pass false at each per-assembly call and invoke
    /// WireFieldTriggerHandlersAll itself exactly once after every assembly has
    /// loaded. Calling it once per assembly there was both slower — O(apps x
    /// table-count) since each call re-walks the same growing table set — and wrong:
    /// a table whose owning app hasn't loaded yet resolves to nothing on an early
    /// call, and (before this fix) got marked wired anyway, permanently skipping the
    /// real wiring once that app's assembly did load.
    /// </param>
    public static void SetTestAssembly(Assembly asm, bool wireFieldTriggers = true)
    {
        if (_currentTestAssembly == asm)
        {
            if (wireFieldTriggers)
                AlRunner.Patches.RecordPatches.WireFieldTriggerHandlersAll();
            return;
        }
        _currentTestAssembly = asm;
        // Superseding registration for asm's OWN simple name — see
        // _latestGenerationByAssemblyName's doc comment (#1901). Unconditional: this must
        // happen for every app SetTestAssembly loads, not only whichever one ends up being
        // CurrentTestAssembly when a cross-app call actually needs to resolve it.
        RegisterAssemblyGeneration(asm);
        _codeunitTypeCache.Clear();
        // NavApp.GetResource: bind this emitted assembly to the current bundle dir
        // (its app.json resourceFolders are where the app's resource bytes live).
        AlRunner.Patches.NavAppResourcePatches.RegisterTestAssembly(asm);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // NavObjectDictionary`2.get_Target used to be hooked here, per closed
        // instantiation, once the test assembly's generic types were in the AppDomain.
        // It is now rewritten once on the open generic by NclCecilRewrite, so there is
        // nothing assembly-specific left to do — which also fixes the two cases the
        // scan could never reach: dictionaries in dependency assemblies (never passed
        // to SetTestAssembly) and dictionaries that are only method locals (not
        // discoverable as a field or property type).
        // Hook XmlPort{ID}.InitializeComponent() overrides in the test assembly.
        // The BC-generated InitializeComponent calls EndInitialization() which may be
        // inlined by the JIT into the caller — hooking EndInitialization() in NCL is
        // unreliable. Hooking the override directly (on the concrete XmlPort type in
        // the test assembly) is deterministic since the JIT hasn't seen this method yet.
        sw.Restart();
        HookXmlPortInitializeComponents(asm);
        AlRunner.PerfTrace.Log($"SetTestAssembly.HookXmlPortInitializeComponents {sw.ElapsedMilliseconds}ms");

        // Field-level OnValidate/OnLookup wiring. NCLMetaField.EventTriggerDataValue
        // must point at the AL-emitted [FieldTriggerHandler] methods on the Record CLR
        // class. The NCLMetaTable was built during AddSourceDir (before AL emit), so
        // the Record CLR type didn't exist yet — we delay wiring to here, the first
        // point at which the AL-emitted Record types are loaded into the AppDomain.
        if (wireFieldTriggers)
        {
            sw.Restart();
            AlRunner.Patches.RecordPatches.WireFieldTriggerHandlersAll();
            AlRunner.PerfTrace.Log($"SetTestAssembly.WireFieldTriggerHandlersAll {sw.ElapsedMilliseconds}ms");
        }

        // Enum field-option metadata fix-up: the AlEnumMetadataRegistry is
        // populated only by BcCompiler.Emit, which runs after AddSourceDir.
        // The first BuildNCLMetaTable pass therefore misses enum-typed fields.
        // Re-apply now that the registry has the bucket's emitted enums.
        sw.Restart();
        AlRunner.Patches.RecordPatches.FixupEnumFieldOptionMetadataAll();
        AlRunner.PerfTrace.Log($"SetTestAssembly.FixupEnumFieldOptionMetadataAll {sw.ElapsedMilliseconds}ms");
    }

    // Cached reflection for Codeunit151.initializationInProgress field.
    // Populated on first call to PrimeCodeunit151Instance; null if not yet resolved or not found.
    private static FieldInfo? _fCu151InitializationInProgress;
    private static bool _fCu151Resolved;

    /// <summary>
    /// Populate skeleton state on a freshly-created <c>Codeunit151</c>
    /// (SystemInitializationImpl, SingleInstance=true, System Application): set
    /// <c>initializationInProgress = true</c> so the UNMODIFIED
    /// <c>SystemInitialization.IsInProgress()</c> returns true. This is a skeleton-state
    /// poke, NOT a DLL-body rewrite (precompiled-dll-respect).
    ///
    /// Why true: BaseApp <c>WorkflowEventHandling.AddEventToLibrary</c> (CU 1520) throws
    /// "An event with description X already exists." when an event's UI description
    /// duplicates an existing one, UNLESS <c>IsInProgress()</c> is true (the company-init
    /// registration context, where BC tolerates the collision). <c>IsInProgress()</c> is
    /// only ever READ inside <c>AddEventToLibrary</c> (event registration), never during
    /// test assertions, so always-true has no observable effect on test logic.
    ///
    /// KNOWN LIMITATION (deliberate): this masks a BC-version mismatch. The runner pins
    /// BaseApp 28.1, but apps like RecoverySolutions target <c>application: 26.0.0.0</c>.
    /// BC 28.1 added a native "item journal batch approval" workflow event whose
    /// description collides with such apps' own pre-28 events; on the targeted BC 26 there
    /// is no collision (real CI is green). The faithful fix is version-aware artifact
    /// selection (use the BC major matching each bundle's app.json <c>application</c>);
    /// until then this poke keeps these tests progressing rather than failing on a
    /// conflict that does not exist on the version they actually target.
    ///
    /// Called from <see cref="NavCodeunitHandle_CreateTarget"/> for every Codeunit151
    /// instance — CreateTarget builds a new instance per call (no runner singleton cache),
    /// so the flag must be set right after construction, before the caller reads it.
    /// </summary>
    public static void PrimeCodeunit151Instance(object instance)
    {
        try
        {
            if (!_fCu151Resolved)
            {
                _fCu151Resolved = true;
                const BindingFlags bf = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
                _fCu151InitializationInProgress =
                    instance.GetType().GetField("initializationInProgress", bf)
                    ?? instance.GetType().GetField("InitializationInProgress",   bf);
                if (_fCu151InitializationInProgress == null)
                    Console.Error.WriteLine($"[BcRuntime] PrimeCodeunit151: initializationInProgress field " +
                        $"not found on {instance.GetType().FullName} — IsInProgress() will always be false");
            }
            _fCu151InitializationInProgress?.SetValue(instance, true);
        }
        catch (Exception ex)
        {
            var inner = ex is System.Reflection.TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            Console.Error.WriteLine($"[BcRuntime] PrimeCodeunit151Instance failed: {inner.GetType().Name}: {inner.Message}");
        }
    }

    /// <summary>
    /// Drop every per-bundle, bundle-derived cache so the SAME process can load an
    /// edited bundle of the same identity (server mode) without serving stale
    /// record/codeunit CLR types, metadata, enum registrations, or in-memory rows.
    /// Installed hooks and resolved runtime reflection handles are deliberately
    /// preserved — only state derived from the loaded bundle is cleared. Call this
    /// BEFORE re-registering source dirs + re-emitting + <see cref="SetTestAssembly"/>.
    ///
    /// Scope: code/logic edits (triggers, procedure/codeunit bodies) are picked up
    /// fully. Field-/table-SHAPE edits keep BC's own skeleton NCLMetadata field set
    /// until the server is restarted — we do not clear BC's shared
    /// metadataCacheEntries, which also holds dependency BC-table metadata. See
    /// docs/server-mode.md for the reload contract and this known limitation.
    /// </summary>
    public static void ResetForNewBundleReload()
    {
        _currentTestAssembly = null;
        // AL-output type caches that live on this partial class (CodeunitPatches,
        // XmlPortPatches). Their finders already prefer CurrentTestAssembly; the
        // caches just need dropping so the rebuild re-resolves against the new asm.
        _codeunitTypeCache.Clear();
        _formTypeCache.Clear();
        _reportTypeCache.Clear();
        _queryTypeCache.Clear();
        _xmlPortTypeCache.Clear();
        _metaReportFallbackCache.Clear();
        // Enum option metadata (this partial class) + the emit-time enum registry.
        _alEnumCache.Clear();
        AlEnumMetadataRegistry.Clear();
        AlReportMetadataRegistry.Clear();
        AlReportLayoutRegistry.Clear();
        NavReportSync.ResetMetadataCache();
        // Sibling patch classes with their own bundle-derived state.
        AlRunner.Patches.RecordPatches.ResetForReload();
        AlRunner.Patches.EventSubscriberPatches.ResetForReload();
    }

    private static void HookXmlPortInitializeComponents(Assembly asm)
    {
        var repl = typeof(BcRuntime).GetMethod(nameof(NavXmlPort_InitializeComponent),
            BindingFlags.Public | BindingFlags.Static)!;
        try
        {
            // Only XmlPort* types are candidates, and AssemblyTypeIndex resolves exactly those
            // out of the TypeDef table — no whole-assembly type load just to reject the rest.
            foreach (var t in AlRunner.Infrastructure.AssemblyTypeIndex.For(asm).EnumerateWithPrefix("XmlPort"))
            {
                var m = t.GetMethod("InitializeComponent",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                    null, Type.EmptyTypes, null);
                if (m == null) continue;
                JmpHook.Apply(m, repl, $"{t.Name}.InitializeComponent");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[BcRuntime] HookXmlPortInitializeComponents failed: {ex.Message}");
        }
    }

    // ── Spike 4: EventPipe JIT listener ──────────────────────────────────────────────────────
    public static EventPipeJitListener? JitListener { get; private set; }

    /// <summary>
    /// Starts the EventPipe JIT listener with registered targets.
    /// Must be called BEFORE ForceLoadBcDlls() so we catch methods JIT'd during BC load.
    /// Also called early from Program.cs if needed.
    /// </summary>
    public static void StartJitListener()
    {
        if (JitListener != null) return;
        JitListener = new EventPipeJitListener();

        // Register targets.
        // (A) NavRecord.ALFieldCaptionAsync — the primary async-method target.
        var repl = typeof(BcRuntime).GetMethod(nameof(NavRecord_ALFieldCaptionAsync),
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (repl != null)
            JitListener.AddTarget(
                "Microsoft.Dynamics.Nav.Runtime.NavRecord",
                "ALFieldCaptionAsync",
                repl);
        else
            Console.Error.WriteLine("[Spike4] Warning: NavRecord_ALFieldCaptionAsync replacement method not found");

        // Probe target retained for diagnostic confirmation that the
        // event→target dispatch path reaches TARGET MATCH end-to-end on a
        // method that IS reliably JIT'd (ALFieldCaptionAsync did not fire in
        // a record-table smoke — likely R2R-only-no-JIT in that path).
        // get_ServiceAccount is a known-JIT'd method on NavEnvironment.
        // Safe under DryRun: never actually patched.
        if (repl != null)
        {
            JitListener.AddTarget("Microsoft.Dynamics.Nav.Runtime.NavEnvironment", "get_ServiceAccount", repl);
        }

        JitListener.Enable();
        Console.Error.WriteLine("[Spike4] JIT listener started (targets registered, subscribed)");

        // Dump DryRun stats at process exit so we can verify Phase A plumbing
        // without doing any Console output from the JIT-callback thread.
        AppDomain.CurrentDomain.ProcessExit += (_, __) =>
        {
            try
            {
                var jl = JitListener;
                if (jl == null) return;
                jl.SnapshotCounters();
                Console.Error.WriteLine($"[Spike4] === EventPipe DryRun summary ===");
                Console.Error.WriteLine($"[Spike4] Total MethodLoad events: {jl.TotalMethodLoadEvents}");
                Console.Error.WriteLine($"[Spike4] BC MethodLoad events:    {jl.BcMethodLoadEvents}");
                int i = 0;
                foreach (var s in jl.DryBcSamples)
                {
                    if (++i > 30) break;
                    Console.Error.WriteLine($"[Spike4] BC sample: {s}");
                }
                Console.Error.WriteLine($"[Spike4] Target matches: {jl.DryTargetMatches.Count}");
                foreach (var m in jl.DryTargetMatches)
                    Console.Error.WriteLine($"[Spike4] TARGET MATCH (deferred): {m}");
                Console.Error.WriteLine($"[Spike4] === end ===");
            }
            catch (Exception ex) { Console.Error.WriteLine($"[Spike4] ProcessExit dump failed: {ex.Message}"); }
        };
    }

    public static void EnsureApplied()
    {
        if (_applied) return;
        _applied = true;

        Win32Stubs.Register();

        // Phase A diagnostic-only EventPipe JIT listener. Subscribing to the JIT
        // event stream is known to race with R2R precode patching and intermittently
        // SIGSEGV during early test discovery (~50% rate at HEAD; ~10% with
        // DOTNET_ReadyToRun=0). Bisect (cc7c2cdf) confirmed the race window opened
        // once a perf change made startup ~30% faster. The listener does no
        // production work in DryRun mode — it only logs counts at process exit.
        // Off by default; opt in with AL_RUNNER_JIT_LISTENER=1 for diagnostics.
        if (Environment.GetEnvironmentVariable("AL_RUNNER_JIT_LISTENER") == "1")
        {
            EventPipeJitListener.DryRun = true;
            StartJitListener();
        }

        ForceLoadBcDlls();
        var navNcl = AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");

        // Now that NavNcl is loaded, register the `original` MethodBase for each target
        // so InstallIndirect can use it.
        if (JitListener != null)
        {
            var navRecordType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecord");
            var ep = navRecordType?.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "ALFieldCaptionAsync");
            if (ep != null)
            {
                var repl = typeof(BcRuntime).GetMethod(nameof(NavRecord_ALFieldCaptionAsync),
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (repl != null)
                    JitListener.AddTarget("Microsoft.Dynamics.Nav.Runtime.NavRecord", "ALFieldCaptionAsync", repl, ep);
                Console.Error.WriteLine($"[Spike4] Registered original MethodBase for ALFieldCaptionAsync");
            }
            else
                Console.Error.WriteLine("[Spike4] Warning: ALFieldCaptionAsync not found in NavRecord after load");
        }

        RootTreeStub = new RootTreeObject();
        SuppressEventLogWriter();
        ApplyAllPatches(navNcl);

        // Definitive success signal — workers, future agents, and future-me can grep
        // this single line to verify patch install completed without a silent crash.
        // Any SIGSEGV during ApplyAllPatches above kills the process before this prints,
        // so the absence of this line in stderr is unambiguous proof of a crashed install.
        // JmpHook.LastAttempt names the hook in flight if install crashed mid-stream
        // (visible only with AL_RUNNER_HOOK_TRACE=1, which logs every Apply with flush).
        // Write to both streams + a tmpfile fallback so the success marker survives even
        // if a precedent patch (e.g. NavEnvironment.cctor replacement) redirected Console.
        var ready = $"[BcRuntime] STARTUP-READY: {AlRunner.Infrastructure.JmpHook.AppliedCount} hooks applied";
        Console.Out.WriteLine(ready);
        Console.Out.Flush();
        Console.Error.WriteLine(ready);
        Console.Error.Flush();
        try { System.IO.File.AppendAllText(Path.Combine(Path.GetTempPath(), "al-runner-startup.log"), ready + "\n"); } catch { }

        // Patch install is complete, so the orphan set is final: every Hook(...) site that is
        // owned by neither the (disabled) JmpHook layer nor a Cecil rewrite. Those patches are
        // silently absent at runtime — BC's unpatched body runs instead. AL_RUNNER_HOOK_AUDIT=1
        // names them so the remaining JmpHook→Cecil migration debt is measurable.
        AlRunner.Infrastructure.JmpHook.ReportOrphanedHooks();

        // Wire FirstChanceException-based AL call-stack capture now that patches are live
        // and _skeletonSession is initialised. This must happen after all hooks so that
        // NavException type lookup succeeds and CurrentMethodScope reflection is valid.
        if (_skeletonSession != null)
            AlRunner.Infrastructure.AlCallStackCapture.Initialize(_skeletonSession);
    }

    /// <summary>
    /// Sets `Microsoft.Dynamics.Nav.Types.EventLogWriter.CustomWriter` to a no-op so
    /// `Write(...)` short-circuits before enqueueing into the background thread that
    /// calls into `System.Diagnostics.EventLog.WriteEntry` — which P/Invokes
    /// `kernel32.dll!WaitForSingleObject` from `System.Diagnostics.EventLog.dll`
    /// (an assembly Win32Stubs' Nav-only resolver doesn't cover, so we avoid the
    /// path entirely instead of broadening the resolver scope).
    /// </summary>
    private static void SuppressEventLogWriter()
    {
        var typesAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types");
        var elw = typesAsm?.GetType("Microsoft.Dynamics.Nav.Types.EventLogWriter");
        var prop = elw?.GetProperty("CustomWriter", BindingFlags.Public | BindingFlags.Static);
        if (prop?.SetMethod == null) return;
        // CustomWriter is Action<string, EventLogEntryType, string>. Build a
        // matching no-op via DynamicMethod so we don't have to import
        // System.Diagnostics.EventLog (which is what we're avoiding).
        var args = prop.PropertyType.GetGenericArguments(); // [string, EventLogEntryType, string]
        var dm = new System.Reflection.Emit.DynamicMethod(
            "EventLogNoOpDyn", typeof(void), args, typeof(BcRuntime).Module);
        var il = dm.GetILGenerator();
        il.Emit(System.Reflection.Emit.OpCodes.Ret);
        prop.SetValue(null, dm.CreateDelegate(prop.PropertyType));
    }

    private static void ForceLoadBcDlls()
    {
        var dir = AlRunner.Infrastructure.BcArtifacts.ServiceTierDir;
        // Ncl is preloaded with Cecil rewrite by Program.cs before this runs.
        foreach (var n in new[] { "Microsoft.Dynamics.Nav.Common", "Microsoft.Dynamics.Nav.Types",
                                  "Microsoft.Dynamics.Nav.Language" })
            Assembly.LoadFrom(Path.Combine(dir, n + ".dll"));
        // Sanity: confirm Ncl in the AppDomain is the Cecil-rewritten one (no Location).
        var ncl = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        Console.Error.WriteLine($"[BcRuntime] Ncl in AppDomain: Location='{ncl?.Location}' (empty = byte-array load OK)");
    }

    private static void ApplyAllPatches(Assembly navNcl)
    {
        var envType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavEnvironment")
            ?? throw new InvalidOperationException("NavEnvironment not found");
        _navEnvironmentType = envType;

        // Without this, every page's merged MasterPage arrives with its whole control tree
        // removed — see MetadataProviderElementRemoval.cs.
        AlRunner.Patches.MetadataProviderElementRemoval.Apply(navNcl);

        // NavEnvironment.cctor — replace WindowsIdentity-touching init
        Hook(envType.TypeInitializer!, nameof(NavEnvironmentCctorReplacement), "NavEnvironment..cctor");
        HookProperty(envType, "ServiceAccount", true, nameof(GetServiceAccountReplacement));
        HookProperty(envType, "ServiceAccountName", true, nameof(GetServiceAccountNameReplacement));
        HookMethodIfExists(envType, "EmitServerStartupTraceEvents",
            BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance,
            (m) => m.IsStatic ? nameof(NoOp2) : nameof(NoOp3));

        // No-op `new NavOpenTelemetryLogger(...)` — its ctor opens an OpenTelemetry pipeline that
        // tries to add the Geneva ETW exporter, which throws on Linux. The NavEnvironment ctor
        // assigns the result to NavDiagnostics.OpenTelemetryLogger and never reads members until
        // a trace is sent later (already suppressed via existing trace hooks).
        var navTypesAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types");
        var navOtl = navTypesAsm?.GetType("Microsoft.Dynamics.Nav.Diagnostic.NavOpenTelemetryLogger");
        if (navOtl != null)
        {
            foreach (var c in navOtl.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var ps = c.GetParameters().Length;
                var noop = ps switch { 1 => nameof(NoOp_OneArg), 2 => nameof(NoOp2), 3 => nameof(NoOp3), 4 => nameof(NoOp4), _ => null };
                if (noop != null) Hook(c, noop, $"NavOpenTelemetryLogger..ctor/{ps}");
            }
        }

        // ExecutionListener..cctor — static constructor accesses thread-local/service-tier
        // state on first invocation via NavMethodScope.Run(). Under R2R, the PrestubMethodFrame
        // dispatch races with hooks on adjacent methods and causes SIGSEGV when the test bundle
        // is large (many suites in a combined bucket). Replace the cctor with a safe initialiser
        // that just sets the syncRoot and leaves Instance null — ALFunctionTimingExecutionListener
        // Start/Exit are already no-op'd below, so null Instance is safe.
        var execListenerType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ExecutionListener");
        if (execListenerType?.TypeInitializer != null)
            Hook(execListenerType.TypeInitializer, nameof(ExecutionListenerCctorReplacement), "ExecutionListener..cctor");

        // ExecutionListener — static methods (AsArray, AddListener, RemoveListener etc.)
        // also crash via R2R PrestubMethodFrame in large bundles. No-op the static
        // methods that have no AL semantic effect in headless mode. AsArray is called
        // by CodeCoverageManager; AddListener/RemoveListener are called by
        // ALFunctionTimingExecutionListener registration paths.
        if (execListenerType != null)
        {
            foreach (var m in execListenerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (m.Name is not ("AsArray" or "AddListener" or "RemoveListener" or "AsSingleInstanceOrNull")) continue;
                var ps = m.GetParameters().Length;
                var noop = ps switch { 0 => nameof(NoOp_0Args), 1 => nameof(ReturnNull_OneArg), _ => null };
                if (noop != null) Hook(m, noop, $"ExecutionListener.{m.Name}({ps})");
            }
        }

        // CodeCoverageManager — LoadTableDataIntoCounters and CodeCoverageRecorderForSession
        // access the execution listener and session infrastructure. No-op them; code
        // coverage tracking is not available in headless mode.
        var ccMgrType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.CodeCoverageManager");
        if (ccMgrType != null)
        {
            // LoadTableDataIntoCounters(NavSession) → void — no-op to prevent ExecutionListener.AsArray crash
            var loadTdc = ccMgrType.GetMethod("LoadTableDataIntoCounters", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (loadTdc != null) Hook(loadTdc, nameof(NoOp_OneArg), "CodeCoverageManager.LoadTableDataIntoCounters");
            // StartCodeCoverage(NavSession) → void
            var startCov = ccMgrType.GetMethod("StartCodeCoverage", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (startCov != null) Hook(startCov, nameof(NoOp_OneArg), "CodeCoverageManager.StartCodeCoverage");
            // StopCodeCoverageRecording(NavSession) → void
            var stopCov = ccMgrType.GetMethod("StopCodeCoverageRecording", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (stopCov != null) Hook(stopCov, nameof(NoOp_OneArg), "CodeCoverageManager.StopCodeCoverageRecording");
            // RefreshTable(NavSession) → void
            var refresh = ccMgrType.GetMethod("RefreshTable", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (refresh != null) Hook(refresh, nameof(NoOp_OneArg), "CodeCoverageManager.RefreshTable");
        }

        // Try the real factory first: NavEnvironment.InstantiateStandaloneNavEnvironment(true, false).
        // The cctor replacement above already wired the static `lockObject`/`instanceId`/
        // `serviceInstanceName` so the factory's MonitorLock(lockObject, ...) succeeds.
        // If the ctor throws (Linux-incompatible deps, missing settings file, KeyVault, DB...),
        // fall back to the skeleton so the runner still boots; per-throw JMP-hooks should be
        // added one-by-one until the real ctor runs to completion.
        var instField = envType.GetField("instance", BindingFlags.NonPublic | BindingFlags.Static);
        var factory = envType.GetMethod("InstantiateStandaloneNavEnvironment",
            BindingFlags.NonPublic | BindingFlags.Static);
        bool ctorOk = false;
        if (factory != null)
        {
            try
            {
                factory.Invoke(null, new object[] { true, false });
                ctorOk = instField?.GetValue(null) != null;
                if (ctorOk) Console.Error.WriteLine("[BcRuntime] NavEnvironment ctor: OK (full init)");

                // The NoOp4-hooked NavOpenTelemetryLogger ctor leaves the inner readonly fields
                // (openTelemetryLoggerInstanceForNstLog, ...SpanLoggerInstance, ...) null. The env
                // ctor assigned this half-initialised instance to NavDiagnostics.OpenTelemetryLogger;
                // every trace call routes through `OpenTelemetryLogger?.LogTelemetryEvent(...)` which
                // dispatches to LogTelemetryEventTrace and NREs on the null inner. Setting the static
                // back to null routes through the existing `?.` null-conditional and skips telemetry.
                var navDiagT = navTypesAsm?.GetType("Microsoft.Dynamics.Nav.Diagnostic.NavDiagnostics");
                var pOtl = navDiagT?.GetProperty("OpenTelemetryLogger", BindingFlags.Public | BindingFlags.Static);
                pOtl?.SetValue(null, null);
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                Console.Error.WriteLine("[BcRuntime] NavEnvironment ctor THREW — falling back to skeleton:");
                Console.Error.WriteLine($"  {inner.GetType().FullName}: {inner.Message}");
                var st = new System.Diagnostics.StackTrace(inner, fNeedFileInfo: true);
                for (int fi = 0; fi < st.FrameCount; fi++)
                {
                    var frame = st.GetFrame(fi);
                    var m = frame?.GetMethod();
                    Console.Error.WriteLine($"    [{fi}] IL+0x{frame?.GetILOffset():X4} native+0x{frame?.GetNativeOffset():X4}  {m?.DeclaringType?.FullName}.{m?.Name}({string.Join(",", m?.GetParameters().Select(p=>p.ParameterType.Name) ?? Array.Empty<string>())})");
                }
            }
        }
        if (!ctorOk && instField != null)
        {
            var skel = RuntimeHelpers.GetUninitializedObject(envType);
            var instLock = envType.GetField("lockObject", BindingFlags.NonPublic | BindingFlags.Instance);
            if (instLock != null) instLock.SetValue(skel, new object());
            instField.SetValue(null, skel);
        }
        HookProperty(envType, "Instance", true, nameof(GetInstanceReplacement));

        // NavApplicationObjectBase.get_Session — return skeleton NavSession.
        // Also hook the NavApplicationObjectBase.ctor to inject _skeletonSession directly,
        // because the get_Session property is typically inlined by the JIT.
        var aoType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavApplicationObjectBase");
        var sessType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavSession");
        var msType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavMethodScope");
        var treeObjType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.TreeObject");
        var treeHandlerType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.TreeHandler");
        if (aoType != null && sessType != null)
        {
            _skeletonSession = RuntimeHelpers.GetUninitializedObject(sessType);
            // GetUninitializedObject leaves cultureSettings = default(ClientSettings) — a
            // struct whose every pattern string is null. Because it is a struct, BC's own
            // "no session → use the default" fallback can never fire (see
            // SeedSkeletonRegionalSettings), so every AL Evaluate() into a Date/Time/DateTime
            // NRE'd inside DateTimeParsingHelper. Seed it the way BC seeds its own default.
            SeedSkeletonRegionalSettings(sessType, _skeletonSession!);
            // A BC service tier runs AL on a thread whose culture is the session's culture,
            // and several BC code paths compare Thread.CurrentThread.CurrentCulture.Name
            // against the session's format region. Without this the developer's machine
            // locale leaked into those comparisons, so an AL run could behave differently on
            // a de-DE workstation than in CI. Pin the process to the session culture.
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = RunnerSessionCulture;
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = RunnerSessionCulture;
            Thread.CurrentThread.CurrentCulture = RunnerSessionCulture;
            Thread.CurrentThread.CurrentUICulture = RunnerSessionCulture;
            HookProperty(aoType, "Session", false, nameof(GetSessionReplacement));

            // Plant the skeleton session into RootTreeStub's TreeHandler.session field.
            // TreeHandler.ctor sets `session = parentHandler.session ?? (hostObject as NavSession)`
            // for every child; if the root handler's session is null, every NavCodeunit/NavRecord/etc.
            // created under it ends up with `Tree.Session == null`. BC code that reads
            // `base.Tree.Session.X` (e.g. NavCodeunit.BindSubscription → Session.EventBindings.Add)
            // then NREs. Planting the skeleton here makes the entire test-time tree share one session,
            // matching how a real BC server roots every object under its NavSession.
            if (treeHandlerType != null && RootTreeStub != null)
            {
                var rootHandler = RootTreeStub.Tree;
                _fTreeHandlerSession = treeHandlerType.GetField("session",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (_fTreeHandlerSession != null && rootHandler != null)
                    FieldPoke.SetInstance(_fTreeHandlerSession, rootHandler, _skeletonSession!);
            }

            // Cache fields for the ctor replacement.
            _fAoSession       = aoType.GetField("session",             BindingFlags.NonPublic | BindingFlags.Instance);
            _fAoObjectId      = aoType.GetField("objectId",            BindingFlags.NonPublic | BindingFlags.Instance);
            _fAoOrigGroupId   = aoType.GetField("originalAppGroupId",  BindingFlags.NonPublic | BindingFlags.Instance);
            _fAoRuntimeGroupId= aoType.GetField("runtimeAppGroupId",   BindingFlags.NonPublic | BindingFlags.Instance);

            // NavApplicationObjectBase..ctor is Cecil-owned (see NclCecilRewrite.cs, "Batch 4
            // keystone") — the fields cached above are consumed by the same replacement helper
            // (ApplicationObjectBasePatches.NavApplicationObjectBaseCtorReplacement) from there.
        }
        if (sessType != null)
        {
            // CurrentMethodScope / NavAppGroup / LocalLanguageNoFallback / IsLocalLanguage /
            // GetSecurityFilters / PushDynamicCaptionStack / SyncFormatSettings / get_Culture /
            // get_WindowsCulture are all Cecil-owned (see NclCecilRewrite.cs, NavSession getter
            // cluster) — the NRE reasoning for each lives there now.

            // NavSession.Company getter — NavRecord.GetCompanyNameToken reads Session.Company.CompanyNameToken.
            // Build skeleton NavCompany and inject into both the property and the backing field.
            // Also seed companyName / companyTableId / hasBeenOpened so that the real BC code paths
            // for ALDatabase.ALCompanyName, NavRecord.ALCurrentCompany, and ALCompanyProperty.ALId
            // work without hitting NavRecord(table 2000000006) on the skeleton.
            var navCompanyType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavCompany");
            if (navCompanyType != null)
            {
                var skelCompany = RuntimeHelpers.GetUninitializedObject(navCompanyType);
                var cnTokenField = navCompanyType.GetField("companyNameToken",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                cnTokenField?.SetValue(skelCompany, 0);

                // Seed the internal company name. `session.CompanyName => company.companyName`,
                // so this populates both `ALDatabase.ALCompanyName` (AL `CompanyName()` builtin)
                // and `NavRecord.ALCurrentCompany` (the table-side `CurrentCompany()` builtin),
                // provided session.IsOpen returns true (hasBeenOpened seeded below).
                var companyNameField = navCompanyType.GetField("companyName",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (companyNameField != null)
                    FieldPoke.SetInstance(companyNameField, skelCompany, "My Company");

                // Seed companyTableId with a deterministic non-default NavGuid so
                // NavCompany.CompanyTableId returns immediately (no NavRecord(2000000006)
                // lookup). `ALCompanyProperty.ALId` reads session.CompanyTableId →
                // company.CompanyTableId; the getter short-circuits when this field
                // != NavGuid.Default.
                var companyTableIdField = navCompanyType.GetField("companyTableId",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (companyTableIdField != null)
                {
                    try
                    {
                        var navGuidType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavGuid");
                        if (navGuidType != null)
                        {
                            // Deterministic Guid derived from the stub company name so it stays
                            // stable across runs but is observably non-empty.
                            var stubGuid = new Guid("c0a1bdfa-0000-0000-0000-43524f4e5553"); // 'CRONUS' suffix
                            var ctor = navGuidType.GetConstructor(
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                                null, new[] { typeof(Guid) }, null);
                            if (ctor != null)
                            {
                                var navGuid = ctor.Invoke(new object[] { stubGuid });
                                FieldPoke.SetInstance(companyTableIdField, skelCompany, navGuid);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[BcRuntime] WARN: companyTableId seed failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                _skeletonCompany = skelCompany;
                var companyField = sessType.GetField("company", BindingFlags.NonPublic | BindingFlags.Instance);
                if (companyField != null)
                    FieldPoke.SetInstance(companyField, _skeletonSession!, skelCompany);
            }
            HookProperty(sessType, "Company", false, nameof(GetSkeletonCompanyReplacement));

            // ── ClientTimeZone seed ──────────────────────────────────────────────────────────
            // ALSystemDate.ALRoundDateTime (and similar DateTime helpers) round-trip the value
            // through NavDateTime.ConvertToLocalTime → math → NavDateTime.ConvertToUTc. The two
            // helpers read the session TZ via *different* fallback paths when the backing field
            // is null: ConvertToLocalTime falls back to TimeZoneInfo.Local, while ConvertToUTc
            // routes through NavSessionOrDefaultProvider.GetClientTimeZone which falls back to
            // appInitFallbackValues.DefaultClientTimeZone (UTC on a skeleton AppInit). The
            // asymmetry skews every round-trip by the local UTC offset (CET → +1h winter, CEST →
            // +2h summer), surfacing as off-by-one-hour failures in RoundDateTime tests.
            // Populate the backing field with TimeZoneInfo.Local so both fallback paths land on
            // the same TZ and the round-trip is identity.
            var clientTzField = sessType.GetField("<ClientTimeZone>k__BackingField",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (clientTzField != null)
                FieldPoke.SetInstance(clientTzField, _skeletonSession!, TimeZoneInfo.Local);

            // ── Identity seed: UserId / UserSecurityId / TenantId ────────────────────────────
            // Same R2R-inline trap as CompanyName: ALDatabase.get_ALUserID /
            // ALDatabase.ALUserSecurityId / ALDatabase.ALTenantID are static getters in
            // Ncl that the JIT R2R-bakes — a JmpHook on those entries does not fire
            // (verified by probe spike 2026-05-19). The IL chains are:
            //   get_ALUserID      → NavCurrentThread.Session.User.Name  → NavUser.userName
            //   ALUserSecurityId  → NavCurrentThread.Session.User.Id    → NavUser.userGuid.Value
            //   ALTenantID        → NavCurrentThread.Session.Tenant.Id  → NavTenant.id
            // NavCurrentThread.Session is already wired to _skeletonSession in
            // RecordPatches.WireNavCurrentThreadSession, so the rest is field-poke:
            // populate Authenticator → NavUser (userName + userGuid) and tenant → NavTenant.id.
            try
            {
                var navUserType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavUser");
                var navAuthType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavUserAuthentication");
                var navGuidType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavGuid");
                if (navUserType != null && navAuthType != null && navGuidType != null)
                {
                    // Build skeleton NavUser with userName="TESTUSER" and a deterministic userGuid.
                    // Default user-id matches AlRunner v1's `AlScope.UserId` default ("TESTUSER",
                    // see AlRunner/Runtime/AlScope.cs:248). Guid is stable across runs so
                    // UserSecurityId() callers can compare for equality without flake.
                    var skelUser = RuntimeHelpers.GetUninitializedObject(navUserType);
                    var fUserName = navUserType.GetField("userName", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fUserName != null) FieldPoke.SetInstance(fUserName, skelUser, "TESTUSER");

                    var fFullName = navUserType.GetField("fullName", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fFullName != null) FieldPoke.SetInstance(fFullName, skelUser, "TESTUSER");

                    var fUserGuid = navUserType.GetField("userGuid", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fUserGuid != null)
                    {
                        // Deterministic stub Guid — clearly synthetic ("TESTUSER" ASCII suffix).
                        var stubUserGuid = new Guid("c0a1bdfa-0000-0000-0000-545553545553"); // 'TESTUS' suffix
                        var guidCtor = navGuidType.GetConstructor(
                            BindingFlags.Public | BindingFlags.Instance,
                            null, new[] { typeof(Guid) }, null);
                        if (guidCtor != null)
                        {
                            var navGuid = guidCtor.Invoke(new object[] { stubUserGuid });
                            FieldPoke.SetInstance(fUserGuid, skelUser, navGuid);
                        }
                    }

                    // Build skeleton NavUserAuthentication with navUser = skelUser.
                    var skelAuth = RuntimeHelpers.GetUninitializedObject(navAuthType);
                    var fNavUser = navAuthType.GetField("navUser", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fNavUser != null) FieldPoke.SetInstance(fNavUser, skelAuth, skelUser);
                    var fAuthUserName = navAuthType.GetField("userName", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fAuthUserName != null) FieldPoke.SetInstance(fAuthUserName, skelAuth, "TESTUSER");

                    // Wire Authenticator backing field on the skeleton session.
                    var fAuthenticator = sessType.GetField("<Authenticator>k__BackingField",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fAuthenticator != null)
                        FieldPoke.SetInstance(fAuthenticator, _skeletonSession!, skelAuth);

                    SeedSkeletonAppId(sessType);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[BcRuntime] WARN: identity (user) seed failed: {ex.GetType().Name}: {ex.Message}");
            }

            // Tenant seed — NavSession.tenant.id powers ALDatabase.ALTenantID. ALTenantID
            // also calls session.CheckConnectionIsOpen() which requires IsOpen=true
            // (already seeded above via hasBeenOpened=true).
            //
            // NOT IMPLEMENTED YET: populating a skeleton NavTenant breaks ~466 tests in
            // bucket-1/record-table because DataAccess.CheckCloudStateReadyReplicationAllowOperation
            // is called on every Insert/Find, and its short-circuit is
            //   `if (tenant != null && tenant.IsDatabaseDisposed) return;`
            // — a null `tenant` short-circuits silently, but a non-null uninitialized tenant
            // has Tree==null which makes `get_IsDisposed` return true → IsDatabaseDisposed
            // throws ObjectDisposedException. Faithfully seeding Tree + database + state on
            // NavTenant pulls in the full database-bring-up chain and is out of scope for
            // this identity spike. Left as a follow-up — see HANDOFF "TenantId requires
            // NavTenant.Tree wiring".

            // RuntimeLanguage getter reads `IsOpen ? GlobalLanguage : NavEnvironment.DefaultLanguage`.
            // With IsOpen=true (seeded below), GlobalLanguage is read; it returns cultureSettings.LCID
            // which is 0 on the GetUninitializedObject skeleton — CultureInfo.GetCultureInfo(0) throws.
            // GlobalLanguage / RuntimeLanguage / NavCode.get_Value are all intra-NCL and get R2R-inlined,
            // so JmpHook on the getter doesn't reach. Field-poke `cultureSettings.LCID = 1033` so the
            // inlined caller reads the correct value directly. cultureSettings is a struct — we read it
            // back, write LCID, and reflect-set it (struct write-back semantics).
            try
            {
                var fCultureSettings = sessType.GetField("cultureSettings",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (fCultureSettings != null)
                {
                    var settings = fCultureSettings.GetValue(_skeletonSession);
                    if (settings != null)
                    {
                        var lcidProp = settings.GetType().GetProperty("LCID",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (lcidProp != null && lcidProp.CanWrite)
                            lcidProp.SetValue(settings, 1033);
                        else
                        {
                            // Property may be readonly with a backing field — set the field directly.
                            var lcidField = settings.GetType().GetField("LCID",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                ?? settings.GetType().GetField("<LCID>k__BackingField",
                                    BindingFlags.NonPublic | BindingFlags.Instance);
                            if (lcidField != null)
                            {
                                // Box, write field, reassign struct.
                                var boxed = settings;
                                lcidField.SetValue(boxed, 1033);
                                settings = boxed;
                            }
                        }
                        fCultureSettings.SetValue(_skeletonSession, settings);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[BcRuntime] WARN: cultureSettings.LCID seed failed: {ex.GetType().Name}: {ex.Message}");
            }

            // Seed NavSession.hasBeenOpened = true so session.IsOpen returns true. ALDatabase.ALCompanyName,
            // ALCompanyProperty.ALDisplayName, ALCompanyProperty.ALUrlName, ALCompanyProperty.ALId all
            // check `session.IsOpen` and short-circuit to empty/default otherwise. Field-poke (not JmpHook)
            // because both the getter and its callers live in NCL — R2R inlining could bypass the hook.
            var hasBeenOpenedField = sessType.GetField("hasBeenOpened",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (hasBeenOpenedField != null)
                FieldPoke.SetInstance(hasBeenOpenedField, _skeletonSession!, true);

            // ALCompanyProperty.ALDisplayName — the real body reads from NavRecord on table 2000000006
            // (the Company table) which the skeleton can't serve (no DataAccess for system tables —
            // same gap as RecordLink). The body's fallback (GetCompanyDisplayNameDefaulted) returns
            // companyName when no row is found, so a stub returning "My Company" is observably
            // equivalent to BC running with a Company row whose Display Name is empty. Faithful
            // per docs/scope.md §2 — same justification as RecordLink polyfill.
            // Hooking the static here (AL-callable surface, called from external compiled AL output)
            // dodges the R2R-inlining trap that would catch a hook inside NavCompany.CompanyDisplayName.
            var alCompanyPropType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ALCompanyProperty");
            if (alCompanyPropType != null)
            {
                var alDisplayName = alCompanyPropType.GetMethod("ALDisplayName",
                    BindingFlags.Public | BindingFlags.Static);
                if (alDisplayName != null)
                    Hook(alDisplayName, nameof(ALCompanyProperty_ALDisplayName), "ALCompanyProperty.ALDisplayName");
            }

            // EventBindings — initialized via field initializer
            // (`new List<NavCodeunit>(128)`) on the real ctor, but skeleton session was
            // built via GetUninitializedObject so the backing field is null. Without this
            // init, NavCodeunit.BindSubscription NREs on `Session.EventBindings.Add(this)`.
            var navCuType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavCodeunit");
            var ebBackingField = sessType.GetField("<EventBindings>k__BackingField",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (ebBackingField != null && navCuType != null)
            {
                var listType = typeof(List<>).MakeGenericType(navCuType);
                var listInstance = Activator.CreateInstance(listType, 128);
                FieldPoke.SetInstance(ebBackingField, _skeletonSession!, listInstance);
            }

            // OverriddenAppGroup = NavAppGroup.BaseGroup so NavCurrentThread.TryResolveAppGroup
            // returns BaseGroup instead of dereferencing the uninitialized tenant.NavAppGroup.
            var navAppGroupType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.Apps.NavAppGroup");
            if (navAppGroupType != null)
            {
                var baseGroupField = navAppGroupType.GetField("BaseGroup",
                    BindingFlags.Public | BindingFlags.Static);
                var baseGroup = baseGroupField?.GetValue(null);
                if (baseGroup != null)
                {
                    var overriddenField = sessType.GetField("<OverriddenAppGroup>k__BackingField",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    overriddenField?.SetValue(_skeletonSession, baseGroup);
                }
            }

            // cachedEnvironmentDefaultLcid — `private readonly LazyEx<int>` on NavSession
            // initialized via field initializer to `new LazyEx<int>(() => NavEnvironment.DefaultLanguage)`.
            // GetUninitializedObject skips field initializers → field is null on skeleton.
            // NCLCaptionStrings.GetValueOrDefault(int, NavSession) reads
            //   session.CachedEnvironmentDefaultLanguage  ⇒  cachedEnvironmentDefaultLcid.Value
            // and NREs. Construct a LazyEx<int> that returns NavEnvironment.DefaultLanguage
            // and plant it on the skeleton session so every caller (intra-NCL R2R included)
            // sees a non-null Lazy. Decompile pin: NCL @ 206393, 207228, 146556.
            var cachedLcidField = sessType.GetField("cachedEnvironmentDefaultLcid",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (cachedLcidField != null && cachedLcidField.GetValue(_skeletonSession) == null)
            {
                try
                {
                    var lazyExType = cachedLcidField.FieldType;          // LazyEx<int>
                    var funcOfInt = typeof(Func<>).MakeGenericType(typeof(int));
                    var lazyExCtor = lazyExType.GetConstructor(new[] { funcOfInt });
                    if (lazyExCtor != null)
                    {
                        // Resolve NavEnvironment.DefaultLanguage at call time.
                        var navEnvType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavEnvironment");
                        var defaultLangProp = navEnvType?.GetProperty("DefaultLanguage",
                            BindingFlags.Public | BindingFlags.Static);
                        Func<int> producer = () =>
                        {
                            try
                            {
                                var v = defaultLangProp?.GetValue(null);
                                return v is int i && i > 0 ? i : 1033; // en-US fallback
                            }
                            catch { return 1033; }
                        };
                        // Convert Func<int> producer to the right Func type via Delegate.CreateDelegate?
                        // Simplest: invoke via a lambda using reflection-bound method.
                        var producerDel = Delegate.CreateDelegate(funcOfInt, producer.Target!,
                            producer.Method);
                        var lazyExInstance = lazyExCtor.Invoke(new object[] { producerDel });
                        FieldPoke.SetInstance(cachedLcidField, _skeletonSession!, lazyExInstance);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[BcRuntime] WARN: cachedEnvironmentDefaultLcid populate failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // globalLanguageStack / globalFormatRegionStack — `private readonly List<…> = new()`
            // on NavSession, so GetUninitializedObject leaves both null.
            //
            // They are the session's own language/format-region stacks, and BC pushes onto
            // them whenever AL sets CurrReport.Language or CurrReport.FormatRegion: the
            // assignment routes into Report.ReportLocalLanguageScope.UpdateLanguage, which
            // calls Session.PushLocalLanguage / PushLocalFormatRegion. Null stacks made that
            // a bare NullReferenceException raised from inside BC — so a report that switches
            // language per record (which every Base App document report does; Standard Sales -
            // Invoice sets it from the customer's language code) did not merely ignore the
            // request, it died mid-run.
            //
            // Planting real empty lists is the faithful fix rather than a patched getter:
            // BC's own push/pop/read logic then runs unchanged, so a language set by AL is
            // the language AL reads back, and it is popped at the right scope boundary.
            foreach (var stackFieldName in new[] { "globalLanguageStack", "globalFormatRegionStack" })
            {
                var stackField = sessType.GetField(stackFieldName, BindingFlags.NonPublic | BindingFlags.Instance);
                if (stackField == null || stackField.GetValue(_skeletonSession) != null) continue;
                try
                {
                    FieldPoke.SetInstance(stackField, _skeletonSession!,
                        Activator.CreateInstance(stackField.FieldType)!);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[BcRuntime] WARN: {stackFieldName} populate failed: {ex.GetType().Name}: {ex.Message} — "
                        + "AL that sets CurrReport.Language/FormatRegion will NRE");
                }
            }

            // VerifyExecutePermission overloads → no-op
            foreach (var m in sessType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => m.Name == "VerifyExecutePermission" && m.ReturnType == typeof(void)))
            {
                var p = m.GetParameters().Length;
                var noop = p switch { 1 => nameof(NoOp2), 2 => nameof(NoOp3), _ => null };
                if (noop != null) Hook(m, noop, $"NavSession.VerifyExecutePermission/{p}");
            }

            // NavSession.FlushDataCache hook DISABLED 2026-05-25 pending investigation:
            // post-install execution SIGSEGVs across BOTH al-language and bucket-1 corpora.
            // The hook installs cleanly (283 hooks applied) but the first compile/run after
            // patches crashes in JIT-compiled code with no symbol info. Re-enable only after
            // root-cause: confirm overload signatures and whether FlushDataCache is itself
            // R2R-inlined (which would make the JmpHook silently corrupt the call site).
        }

        // Reflect and cache the fields we need for the ctor replacement below.
        if (treeObjType != null)
            _fTreeObjTree = treeObjType.GetField("tree", BindingFlags.NonPublic | BindingFlags.Instance);
        // NavComplexValue (parent of NavApplicationObjectBase) has its OWN tree field distinct from TreeObject.tree.
        var navComplexValueType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavComplexValue");
        if (navComplexValueType != null)
            _fNavComplexValueTree = navComplexValueType.GetField("tree", BindingFlags.NonPublic | BindingFlags.Instance);
        if (msType != null)
        {
            _fMsSession    = msType.GetField("session",      BindingFlags.NonPublic | BindingFlags.Instance);
            _fMsParentScope= msType.GetField("parentScope",  BindingFlags.NonPublic | BindingFlags.Instance);
            _fMsFlags      = msType.GetField("flags",        BindingFlags.NonPublic | BindingFlags.Instance);
            _fMsStackDepth = msType.GetField("<StackDepth>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            _fMsTopLevelAppObj = msType.GetField("<TopLevelApplicationObject>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        }
        if (sessType != null)
            _fSessCurrentScope = sessType.GetField("<CurrentMethodScope>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        if (treeHandlerType != null)
        {
            _mCreateTreeHandler = treeHandlerType.GetMethod("CreateTreeHandler",
                BindingFlags.Public | BindingFlags.Static);
            // MEMORY LEAK FIX — resolved on treeHandlerType (the base class) so these
            // private fields are found regardless of the concrete handler subtype
            // (TreeObjectHandler/TreeSharedObjectHandler/TreeObjectReferenceHandler).
            _fTreeHandlerParent          = treeHandlerType.GetField("parentHandler",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _fTreeHandlerFirstChildBase  = treeHandlerType.GetField("firstChildHandler",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _fTreeHandlerPrevSibling     = treeHandlerType.GetField("previousSiblingHandler",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _fTreeHandlerNextSiblingBase = treeHandlerType.GetField("nextSiblingHandler",
                BindingFlags.NonPublic | BindingFlags.Instance);
        }

        // Build a proper NavMethodScope+RootMethodScope skeleton so the NavMethodScope ctor
        // (which calls base(parent)) can create child TreeHandlers from it safely.
        // Returning RootTreeObject (an ITreeObject, not NavMethodScope) caused out-of-bounds
        // field reads on a corpus run where heap was fragmented.
        if (msType != null && sessType != null && treeObjType != null && treeHandlerType != null)
        {
            var rootMSType = msType.GetNestedType("RootMethodScope", BindingFlags.NonPublic);
            var createRoot = treeHandlerType.GetMethod("CreateTreeRoot",
                BindingFlags.Public | BindingFlags.Static);
            if (rootMSType != null && createRoot != null)
            {
                var skel = RuntimeHelpers.GetUninitializedObject(rootMSType);
                // CreateTreeRoot(skel) sets parentHandler=null, hostObject=skel.
                // Requires skel.Tree == null (it is — uninitialized) and calls skel.SingleThreaded.
                var rootTree = createRoot.Invoke(null, new object[] { skel });
                // Populate fields so IsDisposed, StackDepth, IsRootScope, etc. work correctly.
                var treeField = treeObjType.GetField("tree", BindingFlags.NonPublic | BindingFlags.Instance);
                var sessionField = msType.GetField("session", BindingFlags.NonPublic | BindingFlags.Instance);
                var flagsField = msType.GetField("flags", BindingFlags.NonPublic | BindingFlags.Instance);
                var depthField = msType.GetField("<StackDepth>k__BackingField",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (treeField != null) FieldPoke.SetInstance(treeField, skel, rootTree);
                if (sessionField != null) FieldPoke.SetInstance(sessionField, skel, _skeletonSession);
                if (flagsField != null) FieldPoke.SetInstance(flagsField, skel, Enum.ToObject(flagsField.FieldType, 1)); // RootScope=1
                if (depthField != null) FieldPoke.SetInstance(depthField, skel, 1);
                _skeletonRootScope = (Microsoft.Dynamics.Nav.Runtime.NavMethodScope)skel;
            }

            // Skeleton NavSession was built with GetUninitializedObject → its inherited
            // TreeObject.tree field is null. Several BC code paths construct TreeHandlers with
            // NavCurrentThread.Session as parent, e.g.:
            //   • NavSession.get_TestExecution → new NavTestExecution(this)
            //   • NavCodeunit.RunCodeunit / ContainsMethodWithAttribute → new NavCodeunitHandle(NavCurrentThread.Session, id)
            //   • ALCompiler.NavIndirectValueToNavValue → new NavScope/NavValue subclasses parented to the session
            // Each of these reaches `TreeHandler..ctor(parent, host)` which throws
            // InvalidOperationException("Parent.Tree cannot be null") when parent.Tree is null.
            // Plant a TreeRoot on the skeleton session so it has a valid Tree the same way the
            // RootMethodScope above does. CreateTreeRoot calls hostObject.Tree (must be null —
            // it is) and hostObject.SingleThreaded (DIM default = false on ITreeObject).
            if (_skeletonSession != null && _fTreeObjTree != null)
            {
                if (_fTreeObjTree.GetValue(_skeletonSession) == null)
                {
                    var sessRootTree = createRoot.Invoke(null, new object[] { _skeletonSession });
                    FieldPoke.SetInstance(_fTreeObjTree, _skeletonSession, sessRootTree);
                }
                {
                    var sessRootTree = _fTreeObjTree.GetValue(_skeletonSession);

                    // TreeHandler.session is assigned ONLY in the `parent != null` branch of
                    // the ctor (`session = parentHandler.session ?? (hostObject as NavSession)`),
                    // and CreateTreeRoot constructs the root with parent == null. So a root
                    // planted this way has a null session field, and because every child
                    // inherits parentHandler.session, `Tree.Session` was null for the WHOLE
                    // tree — including NavTestExecution, whose modal-page dispatch does
                    // `new NavScope(base.Tree.Session)` and got ArgumentNullException(parent).
                    //
                    // The root's host object IS the session, which is precisely the value
                    // BC's own ctor would have computed for it (the `?? (hostObject as
                    // NavSession)` fallback). Set it so the tree can answer what session it
                    // belongs to.
                    // Walk the hierarchy: `session` is private on the TreeHandler BASE class,
                    // and GetField on the concrete TreeObjectHandler does not return private
                    // members of base types.
                    FieldInfo? fHandlerSession = null;
                    for (var wt = sessRootTree!.GetType(); wt != null && fHandlerSession == null; wt = wt.BaseType)
                        fHandlerSession = wt.GetField("session", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fHandlerSession != null && fHandlerSession.GetValue(sessRootTree) == null)
                        FieldPoke.SetInstance(fHandlerSession, sessRootTree, _skeletonSession);
                    else if (fHandlerSession == null)
                        Console.Error.WriteLine(
                            "[BcRuntime] TreeHandler.session field NOT FOUND — Tree.Session stays null, "
                            + "modal-page handler dispatch will fail");
                }
            }
        }

        // After the real NavEnvironment ctor + skeleton root scope are both ready,
        // inject a skeleton NavSystemTenant + NCLMetadata into the real Tenants collection.
        // No-op if env ctor fell back to skeleton (Tenants is null).
        InjectSkeletonSystemTenant(navNcl);

        // Hook the 3-arg NavMethodScope ctor that all generated test-scope nested classes call.
        // The BC ctor body dereferences properties on the skeleton session/root-scope that NRE
        // once earlier-bucket test scopes have mutated shared state (e.g. session.CurrentMethodScope
        // setter writes back, some paths touch Diagnostics, etc.).
        // Replace the whole ctor body with a minimal safe implementation that sets only the
        // fields actually needed for Pass/Fail/Error classification at this layer of the pipeline.
        // NavMethodScope..ctor(3) / ThrowStackOverflow / AssertError / Dispose(bool) are all
        // Cecil-owned (see NclCecilRewrite.cs, "NavMethodScope cluster").

        // TreeHandler.get_Session is Cecil-owned (see NclCecilRewrite.cs block 9b) — the tree's
        // session field is null (the root has no session to propagate), so it returns
        // _skeletonSession instead.

        // ALTelemetryHelper.LogALErrorTelemetry — called before creating NavNCLDialogException;
        // NREs through SessionContextHelper.GetALScope → NavGlobal.get_NCLMetadata on skeleton.
        // No-op is safe because the throw still happens immediately after.
        // The type lives in Microsoft.Dynamics.Nav.Runtime.AL namespace (not Runtime directly).
        foreach (var telTypeName in new[] {
            "Microsoft.Dynamics.Nav.Runtime.ALTelemetryHelper",       // older builds
            "Microsoft.Dynamics.Nav.Runtime.AL.ALTelemetryHelper" })  // 27.x+
        {
            var telType = navNcl.GetType(telTypeName);
            if (telType == null) continue;
            foreach (var m in telType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(m => m.Name == "LogALErrorTelemetry"))
            {
                var p = m.GetParameters().Length;
                var noop = p switch { 2 => nameof(NoOp2), 3 => nameof(NoOp3), 4 => nameof(NoOp4), _ => null };
                if (noop != null) Hook(m, noop, $"ALTelemetryHelper.LogALErrorTelemetry/{p}");
            }
        }

        // SessionTransactionExtensions.Rollback is Cecil-owned (see NclCecilRewrite.cs block
        // 8f). NavMethodScope.AssertError calls it after catching an AL error, and it is what
        // unwinds the row store to the last commit point.

        // NCLEnumMetadata.Create(int), NavCodeunitHandle.CreateTarget, NavCodeunit.get_MetaCodeunit,
        // and NCLMetaCodeunit.get_IsEventManualBinding are all Cecil-owned (see NclCecilRewrite.cs).

        // NavDataTransfer.SetTables — uses NCLMetadata.GetMetaTableById to validate source/dest
        // tables before staging the transfer. Validation is meaningless in headless mode (the
        // actual data move happens via patched RecordImpl). No-op so AL DataTransfer.SetTables
        // calls succeed and downstream Add{Constant,Field,Source}Value can proceed against
        // skeleton-managed buffers.
        var navDataTransferType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavDataTransfer");
        if (navDataTransferType != null)
        {
            var setTables = navDataTransferType.GetMethod("SetTables",
                BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(int), typeof(int) }, null);
            if (setTables != null)
                Hook(setTables, nameof(NoOp3), "NavDataTransfer.SetTables");

            // The AL `DataTransfer.{AddFieldValue,AddConstantValue,AddSourceFilter,AddJoin,
            // CopyFields,CopyRows,Clear}` builtins are not usable outside upgrade/install code.
            // Throw a BC exception so AL `asserterror` observes the same contract.
            var thrownNames = new System.Collections.Generic.HashSet<string> {
                "AddFieldValue", "AddConstantValue", "AddSourceFilter", "AddJoin",
                "CopyFields", "CopyRows", "Clear"
            };
            var hookNames = new System.Collections.Generic.List<string>(
                new[] { "AddFieldValue", "AddConstantValue", "AddSourceFilter",
                        "AddJoin", "CopyFields", "CopyRows", "Clear" });
            foreach (var name in hookNames)
            {
                var m = navDataTransferType.GetMethod(name,
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (m == null) continue;
                var ps = m.GetParameters().Length;
                bool throwHere = thrownNames.Contains(name);
                string? hook;
                if (m.ReturnType == typeof(void))
                {
                    hook = ps switch
                    {
                        0 => throwHere ? nameof(ThrowDataTransfer_OneArg) : nameof(NoOp_OneArg),
                        1 => throwHere ? nameof(ThrowDataTransfer_2Args) : nameof(NoOp2),
                        2 => throwHere ? nameof(ThrowDataTransfer_3Args) : nameof(NoOp3),
                        3 => throwHere ? nameof(ThrowDataTransfer_4Args) : nameof(NoOp4),
                        _ => null
                    };
                }
                else if (m.ReturnType == typeof(int))
                {
                    hook = ps switch
                    {
                        0 => throwHere ? nameof(ThrowDataTransferReturnInt_OneArg) : nameof(ReturnZero_OneArg),
                        _ => null
                    };
                }
                else hook = null;
                if (hook != null) Hook(m, hook, $"NavDataTransfer.{name}");
            }
        }

        // ALTaskScheduler.CheckCodeUnit / ALCanCreateTask / CanCreateTask (scope.md §3.6,
        // #1733) are now Cecil-owned (see NclCecilRewrite.cs, CecilOwned + the ALTaskScheduler
        // block in RewriteNcl). This JmpHook registration used to live here as a no-op for
        // CheckCodeUnit, but JmpHook is off by default (Cecil-only) — the registration was
        // silently dead, and BC's real CheckCodeUnit body ran and threw a codeunit-resolution
        // error before ever reaching CanCreateTask. Deleted rather than left as a redundant
        // Hook(...) call site (JmpHook.Apply auto-skips Cecil-owned keys anyway, but a call
        // site with no effect either way is dead code the audit would just flag).

        // ALMethodScope.AssignScopeId is Cecil-owned (see NclCecilRewrite.cs, "NavMethodScope
        // cluster") — chains through Session.NCLMetadata which is null; no-op leaves scopeId =
        // null which is tolerated by the ScopeId getter.

        // ALSystemErrorHandling.get_AL{GetLastErrorText,GetLastErrorCode,GetLastErrorCallStack}
        // and ALClearLastError — real getters chain through NavCurrentThread.Session which is
        // null on the skeleton thread. Hook to read/clear via the skeleton session directly.
        var alSysErrType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ALSystemErrorHandling");
        if (alSysErrType != null)
        {
            void HookAlErrProp(string propName, string replName, string desc)
            {
                var p = alSysErrType.GetProperty(propName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var g = p?.GetGetMethod(true);
                if (g != null) Hook(g, replName, desc);
            }
            HookAlErrProp("ALGetLastErrorText",     nameof(ALSystemErrorHandling_get_ALGetLastErrorText),     "ALSystemErrorHandling.get_ALGetLastErrorText");
            HookAlErrProp("ALGetLastErrorCode",     nameof(ALSystemErrorHandling_get_ALGetLastErrorCode),     "ALSystemErrorHandling.get_ALGetLastErrorCode");
            HookAlErrProp("ALGetLastErrorCallStack",nameof(ALSystemErrorHandling_get_ALGetLastErrorCallStack),"ALSystemErrorHandling.get_ALGetLastErrorCallStack");
            var clearMethod = alSysErrType.GetMethod("ALClearLastError",
                BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (clearMethod != null)
                Hook(clearMethod, nameof(ALSystemErrorHandling_ALClearLastError), "ALSystemErrorHandling.ALClearLastError");
        }

        // NavIntegerFormatter.FormatWithFormatNumber — value passed via NavValue[] varargs
        // is sometimes null on the skeleton runtime; real body NREs on value.ToInt32().
        var navIntFmtType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavIntegerFormatter");
        if (navIntFmtType != null)
        {
            var fmtMethod = navIntFmtType.GetMethod("FormatWithFormatNumber",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (fmtMethod != null)
                Hook(fmtMethod, nameof(NavIntegerFormatter_FormatWithFormatNumber),
                    "NavIntegerFormatter.FormatWithFormatNumber");
        }

        // NavTestPageHandle.CreateTarget, NavTestPageBase.ALGoToRecord and
        // NavTestPageBase.GetMetaTable are Cecil-owned (see NclCecilRewrite.cs).
        // GetMetaTable used to be hooked here; the JmpHook layer is off by default, so the
        // registration was a silent no-op and BC's own body ran and NREd instead.

        // NavFormHandle.CreateTarget is Cecil-owned (see NclCecilRewrite.cs).
        var formHandleType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavFormHandle");
        if (formHandleType != null)
        {
            // NavFormHandle.Run — Page variable .Run() — non-modal UI (§3.11 OOS).
            // Uses Apply() which patches both the precode and the R2R native code, ensuring
            // callers that resolve directly to native code are also intercepted.
            foreach (var runMethod in formHandleType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "Run"))
            {
                var ps = runMethod.GetParameters();
                var replName = ps.Length switch
                {
                    0 => nameof(FormPatches.NavFormHandle_Run_0),
                    1 => nameof(FormPatches.NavFormHandle_Run_1),
                    2 => nameof(FormPatches.NavFormHandle_Run_2),
                    _ => null
                };
                if (replName == null) continue;
                var repl = typeof(FormPatches).GetMethod(replName, BindingFlags.Public | BindingFlags.Static)!;
                Hook(runMethod, repl, $"NavFormHandle.Run/{ps.Length}p");
            }
        }

        // NavForm.RunModalAsync — PAGE-REPORT-CLUSTERS §2. Hook all 7 overloads
        // (3 instance + 4 static) to return FormResult.OK without touching skeleton
        // session state. Static overloads have no `self` parameter.
        var navFormRuntimeType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavForm");
        if (navFormRuntimeType != null)
        {
            int runModalHooked = 0;
            foreach (var m in navFormRuntimeType.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m2 => m2.Name == "RunModalAsync"))
            {
                var ps = m.GetParameters();
                string? replName = null;
                if (!m.IsStatic)
                {
                    replName = ps.Length switch
                    {
                        0 => nameof(FormPatches.NavForm_RunModalAsync_0),
                        1 => nameof(FormPatches.NavForm_RunModalAsync_1),
                        2 => nameof(FormPatches.NavForm_RunModalAsync_2),
                        _ => null
                    };
                }
                else
                {
                    replName = ps.Length switch
                    {
                        3 => nameof(FormPatches.NavForm_RunModalAsync_S3),
                        4 => nameof(FormPatches.NavForm_RunModalAsync_S4),
                        5 when ps[4].ParameterType == typeof(int)
                            => nameof(FormPatches.NavForm_RunModalAsync_S5n),
                        5 => nameof(FormPatches.NavForm_RunModalAsync_S5f),
                        _ => null
                    };
                }
                if (replName == null) continue;
                var repl = typeof(FormPatches).GetMethod(replName, BindingFlags.Public | BindingFlags.Static);
                if (repl == null)
                {
                    Console.Error.WriteLine($"[BcRuntime] NavForm.RunModalAsync repl not found: {replName}");
                    continue;
                }
                Hook(m, repl, $"NavForm.RunModalAsync/{(m.IsStatic ? "static" : "inst")}/{ps.Length}p");
                runModalHooked++;
            }
            Console.Error.WriteLine($"[BcRuntime] NavForm.RunModalAsync: {runModalHooked} overloads hooked");
        }

        // NavFilterPageBuilder.RunModalAsync — PAGE-REPORT-CLUSTERS §3. Hook the one
        // instance overload RunModalAsync(ITreeObject) to return Action.Ok without
        // touching skeleton ITreeObject/Tree/Session state.
        var navFilterPageBuilderType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavFilterPageBuilder");
        if (navFilterPageBuilderType != null)
        {
            int filterRunModalHooked = 0;
            foreach (var m in navFilterPageBuilderType.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m2 => m2.Name == "RunModalAsync"))
            {
                var ps = m.GetParameters();
                var repl = typeof(FormPatches).GetMethod(
                    nameof(FormPatches.NavFilterPageBuilder_RunModalAsync),
                    BindingFlags.Public | BindingFlags.Static);
                if (repl == null)
                {
                    Console.Error.WriteLine($"[BcRuntime] NavFilterPageBuilder.RunModalAsync repl not found");
                    continue;
                }
                Hook(m, repl, $"NavFilterPageBuilder.RunModalAsync/inst/{ps.Length}p");
                filterRunModalHooked++;
            }
            Console.Error.WriteLine($"[BcRuntime] NavFilterPageBuilder.RunModalAsync: {filterRunModalHooked} overloads hooked");
        }

        // NavReportHandle.CreateTarget and NavQueryHandle.CreateTarget are Cecil-owned (see
        // NclCecilRewrite.cs, CreateTarget family).

        // REPORT.RUN(id [, reqPage [, sysPrinter [, record]]]) / REPORT.RUNMODAL(...) in AL
        // compile to the static NavReport.Run(int, ...) / RunModal(int, ...) overloads.
        // #1771: these used to be JmpHook targets here (NavReport_StaticRun1..4 /
        // NavReport_StaticRunModal1..4 in ReportPatches.cs, each throwing an OOS
        // InvalidOperationException). That JmpHook never fired under the default
        // Cecil-only runtime — JmpHook.Apply silently skips any target that is not
        // Cecil-owned unless AL_RUNNER_ENABLE_JMPHOOK=1 — so the static call fell straight
        // into the Cecil-rewritten `ret` body and silently did nothing (false PASS with 0
        // dataset iterations). Migrated to a direct Cecil-emitted call to
        // NavReportSync.SyncStaticRun (see NclCecilRewrite.cs §NavReport block); no JmpHook
        // needed, same as instance Run()/RunModal() below.

        // ALDatabase.ALSid — BC's real getter walks NavCurrentThread.Session.Identity
        // and NREs on the skeleton (no real session). Hook to return a constant stub
        // SID. The JmpHook fires reliably for this static (R2R spike, 2026-05-18). See
        // AlRunner/Patches/ALDatabasePatches.cs for the rationale.
        var alDbType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ALDatabase");
        if (alDbType != null)
        {
            var alSid = alDbType.GetMethod("ALSid",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (alSid != null)
            {
                var repl = typeof(AlRunner.Patches.ALDatabasePatches)
                    .GetMethod(nameof(AlRunner.Patches.ALDatabasePatches.ALDatabase_ALSid),
                        BindingFlags.Public | BindingFlags.Static);
                if (repl != null)
                {
                    AlRunner.Infrastructure.JmpHook.Apply(alSid, repl, "ALDatabase.ALSid");
                    Console.Error.WriteLine("[BcRuntime] hooking ALDatabase.ALSid");
                }
            }

            // ALDatabase.ALSessionID — same issue: reaches into NavCurrentThread.Session.
            // No parameters; returns int. Hook returns fixed positive stub (42).
            var alSessionId = alDbType.GetMethod("ALSessionID",
                BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (alSessionId != null)
            {
                var repl = typeof(AlRunner.Patches.ALDatabasePatches)
                    .GetMethod(nameof(AlRunner.Patches.ALDatabasePatches.ALDatabase_ALSessionID),
                        BindingFlags.Public | BindingFlags.Static);
                if (repl != null)
                {
                    AlRunner.Infrastructure.JmpHook.Apply(alSessionId, repl, "ALDatabase.ALSessionID");
                    Console.Error.WriteLine("[BcRuntime] hooking ALDatabase.ALSessionID");
                }
            }

            // DISABLED: ALTenantID is R2R-inlined; JmpHook.Apply(PrepareMethod) SIGSEGVs.
            // Cecil rewrite in NclCecilRewrite.RewriteNcl() replaces the body instead.
            // See https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/1617
            // var alTenantId = alDbType.GetMethod("ALTenantID",
            //     BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            // if (alTenantId != null)
            // {
            //     var repl = typeof(AlRunner.Patches.ALDatabasePatches)
            //         .GetMethod(nameof(AlRunner.Patches.ALDatabasePatches.ALDatabase_ALTenantID),
            //             BindingFlags.Public | BindingFlags.Static);
            //     if (repl != null)
            //     {
            //         AlRunner.Infrastructure.JmpHook.Apply(alTenantId, repl, "ALDatabase.ALTenantID");
            //         Console.Error.WriteLine("[BcRuntime] hooking ALDatabase.ALTenantID");
            //     }
            // }

            // ALDatabase.ALServiceInstanceID — NOT HOOKED. Probe-verified 2026-05-19:
            // the JmpHook registration succeeds but the replacement body never fires;
            // the call site is R2R-baked / inlined and returns the default 0. Per
            // .claude/rules/loud-failures.md we do NOT silently install a hook that
            // doesn't intercept. Faithfulness gap: Database.ServiceInstanceId returns 0
            // on the skeleton runtime. Tracked separately.

            // DISABLED: Cecil rewrite in NclCecilRewrite.RewriteNcl() now replaces
            // ALCommit / ALRegisterTableConnection / ALUnregisterTableConnection bodies
            // with no-op IL directly. Avoids PrepareMethod/JmpHook risk on tiny R2R statics.
            // var alCommit = alDbType.GetMethod("ALCommit",
            //     BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            // if (alCommit != null)
            //     Hook(alCommit, nameof(NoOp_0Args), "ALDatabase.ALCommit");

            // foreach (var m in alDbType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            // {
            //     if (m.Name != "ALRegisterTableConnection") continue;
            //     var ps = m.GetParameters().Length;
            //     var noop = ps switch { 3 => nameof(NoOp3), 4 => nameof(NoOp4), _ => null };
            //     if (noop != null) Hook(m, noop, $"ALDatabase.ALRegisterTableConnection({ps} args)");
            // }

            // var alUnregister = alDbType.GetMethod("ALUnregisterTableConnection",
            //     BindingFlags.Public | BindingFlags.Static);
            // if (alUnregister != null)
            //     Hook(alUnregister, nameof(NoOp2), "ALDatabase.ALUnregisterTableConnection");

            // DO NOT hook ALDatabase.ALSelectLatestVersion — its precode is R2R-inlined into
            // callers; the hook installs successfully (no crash log) but executing those
            // callers later SIGSEGVs the runner. See feedback_aldatabase_hard.md.
            // Note 2026-05-25: hooking NavSession.FlushDataCache (the immediate callee one
            // level deeper) ALSO crashes — install succeeds, first test execution SIGSEGVs.
            // The SelectLatestVersion sub-cluster remains open until an architectural fix
            // (EventPipe post-JIT body patch, or skeleton state populated upstream).
        }

        // ALSystemOperatingSystem.GetUrlCore / ALGetUrlInternal / ALGetUrl are Cecil-owned
        // (see NclCecilRewrite.cs) — real bodies reach into ALSession, NavEnvironment.Instance.
        // Tenants, and NavCurrentThread.Session.Tenant.Id, all of which NRE on the skeleton
        // session. Faithful per docs/scope.md: tests that parse/verify real tenant/endpoint
        // URLs are out of scope; tests assert only that the returned string is non-empty.

        // ── Cluster batch: small NREs around session/db skeleton ─────────────
        // Each hooked method below either reaches NavCurrentThread.Session.*,
        // NavGlobal.Database / Tenant, the DataAccessSource provider chain, or
        // notification/dialog infrastructure that the headless runner does not
        // have. Faithful no-op / sentinel-value replacements per docs/scope.md:
        // AL code must continue past the call; tests that verify real side
        // effects (file info, table connection strings, lock-timeout
        // enforcement, password change, task scheduling, dialog drawing) are
        // out of scope.
        if (alDbType != null)
        {
            // ALDatabase.get_ALSerialNumber — reaches session.License (null on skeleton).
            var alSerialGetter = alDbType.GetMethod("get_ALSerialNumber",
                BindingFlags.Public | BindingFlags.Static);
            if (alSerialGetter != null)
                Hook(alSerialGetter, nameof(ReturnStandalone_0Args), "ALDatabase.get_ALSerialNumber");

            // ALDatabase.ALChangeUserPassword — both the void(2-arg) terminal and
            // the bool(3-arg) DataError wrapper that delegates to it. Real body
            // hits PermissionManagement / NavTenant.Database NRE chains.
            foreach (var m in alDbType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "ALChangeUserPassword") continue;
                var ps = m.GetParameters().Length;
                if (ps == 2 && m.ReturnType == typeof(void))
                    Hook(m, nameof(NoOp2), "ALDatabase.ALChangeUserPassword(2)");
                else if (ps == 3 && m.ReturnType == typeof(bool))
                    Hook(m, nameof(ReturnTrue_ThreeArgs), "ALDatabase.ALChangeUserPassword(3)");
            }

            // ALDatabase.ALSetUserPassword — same as ALChangeUserPassword.
            // SessionHasSuperOrSecurityPermissionsForUser NREs on skeleton session.
            foreach (var m in alDbType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "ALSetUserPassword") continue;
                var ps = m.GetParameters().Length;
                if (ps == 2 && m.ReturnType == typeof(void))
                    Hook(m, nameof(NoOp2), "ALDatabase.ALSetUserPassword(2)");
                else if (ps == 3 && m.ReturnType == typeof(bool))
                    Hook(m, nameof(ReturnTrue_ThreeArgs), "ALDatabase.ALSetUserPassword(3)");
            }

            // ALDatabase.ALDataFileInformation — 10-arg bool. Real body reads the
            // ServerUserSettings dataFileInfo XML; not present in headless mode.
            var alDataFile = alDbType.GetMethod("ALDataFileInformation",
                BindingFlags.Public | BindingFlags.Static);
            if (alDataFile != null && alDataFile.GetParameters().Length == 10)
                Hook(alDataFile, nameof(ReturnFalse_10Args), "ALDatabase.ALDataFileInformation");

            // ALDatabase.ALGetDefaultTableConnection — returns "" (no connections registered).
            var alGetDefConn = alDbType.GetMethod("ALGetDefaultTableConnection",
                BindingFlags.Public | BindingFlags.Static);
            if (alGetDefConn != null)
                Hook(alGetDefConn, nameof(ReturnEmptyString_OneArg), "ALDatabase.ALGetDefaultTableConnection");

            // ALDatabase.{get,set}_ALLockTimeout / {get,set}_ALLockTimeoutDuration —
            // each calls DataAccessSource.CreateTenantDataProvider() → SqlTableDataProvider
            // ctor which NREs on session.Database. No SQL backend in headless mode;
            // make these property pairs trivial.
            var lockTOSet = alDbType.GetMethod("set_ALLockTimeout",
                BindingFlags.Public | BindingFlags.Static);
            if (lockTOSet != null) Hook(lockTOSet, nameof(NoOp_OneArg), "ALDatabase.set_ALLockTimeout");
            var lockTOGet = alDbType.GetMethod("get_ALLockTimeout",
                BindingFlags.Public | BindingFlags.Static);
            if (lockTOGet != null) Hook(lockTOGet, nameof(ReturnFalse_0Args), "ALDatabase.get_ALLockTimeout");
            var lockTODurSet = alDbType.GetMethod("set_ALLockTimeoutDuration",
                BindingFlags.Public | BindingFlags.Static);
            if (lockTODurSet != null) Hook(lockTODurSet, nameof(NoOp_OneArg), "ALDatabase.set_ALLockTimeoutDuration");
            var lockTODurGet = alDbType.GetMethod("get_ALLockTimeoutDuration",
                BindingFlags.Public | BindingFlags.Static);
            if (lockTODurGet != null) Hook(lockTODurGet, nameof(ReturnZero_0Args), "ALDatabase.get_ALLockTimeoutDuration");
        }

        // ALTaskScheduler.CanCreateTask(NavSession) is Cecil-owned (see NclCecilRewrite.cs,
        // scope.md §3.6, #1733): rewritten to return false — faithful, the runner has no
        // scheduler. A JmpHook registration used to live here trying to make it return TRUE
        // instead, which directly contradicted the documented/Cecil behaviour; it was always
        // dead (JmpHook is off by default), which is exactly how it went unnoticed.

        // NavDialog.ALClose() / ALUpdateAsync are Cecil-owned (see NclCecilRewrite.cs).

        // NavForm.ObjectID(bool useCaption) — instance string getter. Real body
        // reaches into MetaForm metadata; for fake/temp forms it NREs.
        var navFormType_b = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavForm");
        if (navFormType_b != null)
        {
            var objectId = navFormType_b.GetMethod("ObjectID",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
            if (objectId != null)
                Hook(objectId, nameof(ReturnEmptyString_TwoArgs), "NavForm.ObjectID(bool)");
        }

        // NavRecord.ValidateTruncateSupport, PermissionManagement.
        // SessionHasSuperOrSecurityPermissionsForUser, RecordImplementation.SetSecurityFiltering,
        // and DataProvider.TruncateAsync are all Cecil-owned (see NclCecilRewrite.cs).

        if (alDbType != null)
        {
            // ALDatabase.ALImportData(DataError, bool, ByRef<NavText>, bool, bool, NavRecord, bool)
            // — 7 args returning bool. Real body reaches into Database.ImportData
            // which needs a real backup file; tests only verify the call signature.
            foreach (var m in alDbType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "ALImportData") continue;
                if (m.GetParameters().Length == 7)
                    Hook(m, nameof(ReturnFalse_7Args), "ALDatabase.ALImportData");
            }

            // ALDatabase.ALAlterKeyAsync(NavSession, NavKeyRef, bool) — async
            // ValueTask wrapper around key-alteration; no metadata mutation in
            // headless runner. Hook to a default completed ValueTask.
            var alAlterKey = alDbType.GetMethod("ALAlterKeyAsync",
                BindingFlags.Public | BindingFlags.Static);
            if (alAlterKey != null && alAlterKey.GetParameters().Length == 3)
                Hook(alAlterKey, nameof(ReturnValueTask3), "ALDatabase.ALAlterKeyAsync");

            // DISABLED: ALTenantID is R2R-inlined; JmpHook.Apply(PrepareMethod) SIGSEGVs.
            // Cecil rewrite in NclCecilRewrite.RewriteNcl() replaces the body instead.
            // See https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/1617
            // var alTenant = alDbType.GetMethod("ALTenantID",
            //     BindingFlags.Public | BindingFlags.Static);
            // if (alTenant != null && alTenant.GetParameters().Length == 0)
            //     Hook(alTenant, nameof(ReturnStandalone_0Args), "ALDatabase.ALTenantID");

            // ALDatabase.ALLastUsedRowVersion() / ALMinimumActiveRowVersion() are now
            // Cecil-owned (NclCecilRewrite, backed by the monotonic clock in
            // ALDatabasePatches). The JmpHook registrations that used to live here
            // returned NavBigInteger zero and were orphaned — never applied once the
            // JmpHook layer went off by default — so BC's SQL body ran and NRE'd. Zero
            // was also wrong: BC's @@DBTS is strictly positive.
        }

        // ALTaskScheduler.ALCreateTaskAsync is deliberately LEFT UNMODIFIED (see
        // NclCecilRewrite.cs, scope.md §3.6, #1733): its real body already throws BC's own
        // NavCreateScheduledTasksNotAllowedException once CanCreateTask/CheckCodeUnit are
        // patched to let it reach that gate. A JmpHook registration used to live here trying
        // to make it return a fresh Guid instead — a silent fake suppressing BC's own guard —
        // and was always dead (JmpHook is off by default).

        // SessionTransactionExtensions.SetRecordConsistent / SetRecordInconsistent
        // — extension methods on NavSession that reach DataAccessSource (null on
        // the skeleton). AL semantics: these mark a record's consistency state
        // for a transaction; with no transaction backend, marking is a no-op.
        var sessTxExt = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.SessionTransactionExtensions");
        if (sessTxExt != null)
        {
            foreach (var m in sessTxExt.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "SetRecordConsistent" && m.Name != "SetRecordInconsistent") continue;
                if (m.GetParameters().Length == 2)
                    Hook(m, nameof(NoOp2), "SessionTransactionExtensions." + m.Name);
            }
        }

        // ALNavApp.ALListResources — needs OwningApp metadata which the
        // headless runner has none of. Real body itself returns an empty list
        // when metadata is absent (its 3 fall-through paths do exactly that),
        // so an empty NavList<NavText> is the faithful answer.
        var alNavAppType_b = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ALNavApp");
        if (alNavAppType_b != null)
        {
            var listResources = alNavAppType_b.GetMethod("ALListResources",
                BindingFlags.Public | BindingFlags.Static);
            if (listResources != null && listResources.GetParameters().Length == 2)
                Hook(listResources, nameof(ReturnEmptyNavTextList_2Args),
                    "ALNavApp.ALListResources");
        }
        // NavXmlPortHandle.CreateTarget is Cecil-owned (see NclCecilRewrite.cs, CreateTarget family).

        // NavXmlPort instance methods.
        //
        // #1800: Export(DataError)/Import(DataError)/Run()/SetTableView(NavRecord) used to be
        // Hook(...) call sites right here — orphaned, like every other JmpHook registration this
        // issue is about (JmpHook is disabled by default, so none of these ever fired; BC's real,
        // unpatched bodies ran instead). Investigating why they were hooked at all led to trying
        // to Cecil-own them to a hard "not-yet-implemented" throw — but the full al-language
        // corpus run then showed 14 previously-passing tests (Codeunit60206/60207: nested-table
        // export/import, text-variable triggers, auto-update/auto-replace, SetTableView row
        // filtering) regress, because BC's own real bodies for these four already handle
        // well-formed AL usage correctly once construction succeeds (see BeginInitialization
        // below). So the Hook(...) call sites for these four were deleted outright, not just left
        // dead: there is nothing to redirect to, BC's body is already correct, and leaving a dead
        // registration here would misdescribe the intent to the next reader auditing orphans.
        var navXmlPortType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavXmlPort");
        if (navXmlPortType != null)
        {
            var tDataError = navNcl.GetType("Microsoft.Dynamics.Nav.Types.DataError")
                ?? AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                    .FirstOrDefault(t => t.Name == "DataError");
            var xmlPortNavRecordType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecord");

            // Static XMLPORT.EXPORT(id, stream) / XMLPORT.IMPORT(id, stream) overloads — NOT
            // covered by the #1800 investigation above (that was scoped to the four instance
            // methods; no corpus test was found exercising this static surface either way), so
            // left as-is pending a dedicated follow-up. See tests/runner-extras/standalone-suites
            // /xmlport-cluster-hooks-1800 and the #1800 PR body for the full orphan inventory.
            if (tDataError != null)
            {
                var tNavOutStream = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavOutStream");
                var tNavInStream  = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavInStream");
                var tNavRecord    = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecord");
                if (tNavOutStream != null && tNavRecord != null)
                {
                    var staticExport = navXmlPortType.GetMethod("Export",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new[] { tDataError, typeof(int), tNavOutStream, tNavRecord }, null);
                    if (staticExport != null)
                        Hook(staticExport, nameof(NavXmlPort_StaticExport), "NavXmlPort.Export(DataError,int,NavOutStream,NavRecord)");
                }
                if (tNavInStream != null && tNavRecord != null)
                {
                    var staticImport = navXmlPortType.GetMethod("Import",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new[] { tDataError, typeof(int), tNavInStream, tNavRecord }, null);
                    if (staticImport != null)
                        Hook(staticImport, nameof(NavXmlPort_StaticImport), "NavXmlPort.Import(DataError,int,NavInStream,NavRecord)");
                }
            }

            // DISABLED: NavXmlPort.RunXmlPort() is R2R-compiled/inlined; RuntimeHelpers.PrepareMethod
            // SIGSEGVs the process during patch installation. Cecil migration pending.
            // TODO: convert to Cecil IL rewrite (replace body with RunnerOutOfScopeException throw).
            // See https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/1618
            // var runXmlPortMethod = navXmlPortType.GetMethod("RunXmlPort",
            //     BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            // if (runXmlPortMethod != null)
            //     Hook(runXmlPortMethod, nameof(NavXmlPort_RunXmlPort), "NavXmlPort.RunXmlPort()");

            // XMLPORT.RUN(id [, reqPage [, import [, record]]]) in AL compiles to static
            // NavXmlPort.Run(int, ...) overloads — a genuine, permanent OOS surface (see the
            // canonical comment above NavXmlPort_StaticRun1..4 in
            // AlRunner/Patches/XmlPortPatches.cs). Hook all four overloads to our typed OOS
            // throw. JmpHook itself is disabled by default (Cecil ownership in
            // NclCecilRewrite.cs is what actually fires); registered here too for defence in
            // depth against an AL_RUNNER_ENABLE_JMPHOOK=1 diagnostic pass.
            {
                int staticRunHooked = 0;
                var sr1 = navXmlPortType.GetMethod("Run",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(int) }, null);
                if (sr1 != null) { Hook(sr1, nameof(NavXmlPort_StaticRun1), "NavXmlPort.Run(int)"); staticRunHooked++; }

                var sr2 = navXmlPortType.GetMethod("Run",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(int), typeof(bool) }, null);
                if (sr2 != null) { Hook(sr2, nameof(NavXmlPort_StaticRun2), "NavXmlPort.Run(int,bool)"); staticRunHooked++; }

                var sr3 = navXmlPortType.GetMethod("Run",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(int), typeof(bool), typeof(bool) }, null);
                if (sr3 != null) { Hook(sr3, nameof(NavXmlPort_StaticRun3), "NavXmlPort.Run(int,bool,bool)"); staticRunHooked++; }

                if (xmlPortNavRecordType != null)
                {
                    var sr4 = navXmlPortType.GetMethod("Run",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new[] { typeof(int), typeof(bool), typeof(bool), xmlPortNavRecordType }, null);
                    if (sr4 != null) { Hook(sr4, nameof(NavXmlPort_StaticRun4), "NavXmlPort.Run(int,bool,bool,NavRecord)"); staticRunHooked++; }
                }

                Console.Error.WriteLine($"[BcRuntime] NavXmlPort.Run static overloads: {staticRunHooked} hooked");
            }

            // NavXmlPort.SetTableView(NavRecord) — see the #1800 note above the Export/Import
            // block: no longer hooked here at all. BC's real body works correctly for well-formed
            // usage (row-filtered export, proven by the al-language corpus), so there is nothing
            // to redirect to.

            // BeginInitialization/EndInitialization/Add(TableNode|FieldNode|TextNode) used to
            // be Hook(...) call sites right here — orphaned, like Export/Import/Run/SetTableView
            // above (JmpHook is disabled by default, so none of these ever fired; BC's real,
            // unpatched bodies ran instead). Deleted outright, not left dead: BC's own body is
            // already correct on the skeleton. Full misdiagnosis-and-correction record (an
            // earlier revision Cecil-owned BeginInitialization on a false premise and regressed
            // 14 corpus tests before being reverted) lives once, canonically, in the big comment
            // block above NavXmlPort_StaticRun1..4 in AlRunner/Patches/XmlPortPatches.cs — see
            // there, not here.
            var nclAssembly = navXmlPortType.Assembly;
            var tableNodeType = nclAssembly.GetType("Microsoft.Dynamics.Nav.Runtime.NavXmlPortTableNode");

            // NavXmlPortTableNode(NavRecordHandle) constructor — called from the generated
            // XmlPort{ID}.InitializeComponent() for each tableelement. Calls record.Target which
            // triggers NavRecordHandle.CreateTarget → NCLMetaTable.CreateObjectInstance → the
            // generated Table{ID} ctor → record initialization that NREs before reaching Add().
            // Since Add is already a no-op and we never use the node list, stub ctor as no-op.
            if (tableNodeType != null)
            {
                var xmlPortHandleNavRecordType = nclAssembly.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecordHandle");
                if (xmlPortHandleNavRecordType != null)
                {
                    var tableNodeCtor = tableNodeType.GetConstructor(
                        BindingFlags.Public | BindingFlags.Instance, null, new[] { xmlPortHandleNavRecordType }, null);
                    if (tableNodeCtor != null)
                        Hook(tableNodeCtor, nameof(NavXmlPortTableNode_Ctor), "NavXmlPortTableNode.ctor(NavRecordHandle)");
                }
            }
        }

        // NavFile.ALUpload / ALDownload — browser round-trip (§3.4 file-storage OOS).
        // The stream-based variants (ALUploadIntoStream, ALDownloadFromStream) are in-scope
        // and are left untouched.
        var navFileType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavFile");
        if (navFileType != null)
        {
            foreach (var m in navFileType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "ALUpload" && m.Name != "ALDownload") continue;
                var ps = m.GetParameters();
                if (ps.Length != 6 && ps.Length != 7) continue;
                var replName = (m.Name == "ALUpload" ? "NavFile_ALUpload" : "NavFile_ALDownload")
                    + $"_{ps.Length}";
                Hook(m, replName, $"NavFile.{m.Name}/{ps.Length}p");
            }
        }

        // NCLMetaField.get_FieldCaption — sync underbelly of NavRecord.ALFieldCaptionAsync.
        // Original chains through NavCurrentThread.ResolveAppGroup(Session) →
        // MetaField.GetMergedCaptionMultiLanguage → LanguageProvider/ServerUserSettings,
        // none of which the skeleton runtime initializes. Replace with FieldName, which is
        // what the original returns under FieldIsNotFromMetadata. Lights up the
        // Rec.TestField → ALFieldCaptionAsync error-formatting cascade without hooking the
        // async surface (HANDOFF §5.2 Option C).
        var nclMetaFieldType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaField");
        if (nclMetaFieldType != null)
        {
            var fieldCaptionGetter = nclMetaFieldType.GetProperty("FieldCaption",
                BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod(true);
            if (fieldCaptionGetter != null)
            {
                var repl = typeof(AlRunner.Patches.RecordPatches).GetMethod(
                    nameof(AlRunner.Patches.RecordPatches.NCLMetaField_get_FieldCaption),
                    BindingFlags.Public | BindingFlags.Static)!;
                Hook(fieldCaptionGetter, repl, "NCLMetaField.get_FieldCaption");
            }
        }

        // NavTextConstant.get_Value — every AL Label is emitted as a NavTextConstant. The
        // implicit NavStringValue→string conversion (used by `new NavText(constant)`) reads
        // Value, which dereferences NavCurrentThread.Session → NRE on skeleton thread. Replace
        // with a session-free lookup of the first ENU entry. Lights up Assert codeunit's
        // `LastErrorCode.Contains(testFieldValidationCodeTxt)` and friends (HANDOFF §5.2 Option C).
        var navTextConstantType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavTextConstant");
        if (navTextConstantType != null)
        {
            var valueGetter = navTextConstantType.GetProperty("Value",
                BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod(true);
            if (valueGetter != null)
            {
                var repl = typeof(AlRunner.Patches.RecordPatches).GetMethod(
                    nameof(AlRunner.Patches.RecordPatches.NavTextConstant_get_Value),
                    BindingFlags.Public | BindingFlags.Static)!;
                Hook(valueGetter, repl, "NavTextConstant.get_Value");
            }
        }
        // Also hook NavStringValue.op_Implicit(NavStringValue → string). The C# compiler
        // emits this for every `(string)stringValue` cast, including `new NavText(constant)`.
        // The original is `value?.Value` — a virtual call that JIT may devirtualize+inline,
        // bypassing the get_Value hook above. Patch the static op directly so the dispatch
        // is unconditional regardless of JIT inlining decisions.
        var navStringValueType_forOp = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavStringValue");
        if (navStringValueType_forOp != null)
        {
            var opImplicit = navStringValueType_forOp.GetMethod("op_Implicit",
                BindingFlags.Public | BindingFlags.Static, null,
                new[] { navStringValueType_forOp }, null);
            if (opImplicit != null)
            {
                var repl = typeof(AlRunner.Patches.RecordPatches).GetMethod(
                    nameof(AlRunner.Patches.RecordPatches.NavStringValue_op_Implicit),
                    BindingFlags.Public | BindingFlags.Static)!;
                Hook(opImplicit, repl, "NavStringValue.op_Implicit");
            }
        }

        // NavRecord.TestFieldNotBlank / TestFieldError — sync throw paths of Rec.TestField.
        // Real bodies dereference Session.WindowsCulture, Session.Diagnostics (via
        // TryAddTestFieldAction), Session.Permissions, NavGlobal.NCLMetadata — all null on
        // skeleton runtime. The throw path raises NRE which surfaces as "NullReference"
        // error code and breaks Assert.ExpectedTestFieldError's code-match check.
        // Replace with clean NavTestFieldException factory calls (HANDOFF §5.2 Option C).
        var navRecordType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecord");
        if (navRecordType != null)
        {
            var nclMetaFieldT = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaField");
            var navAlErrorInfoT = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavALErrorInfo");
            if (nclMetaFieldT != null && navAlErrorInfoT != null)
            {
                var testFieldNotBlank = navRecordType.GetMethod("TestFieldNotBlank",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { nclMetaFieldT, navAlErrorInfoT }, null);
                if (testFieldNotBlank != null)
                {
                    var repl = typeof(AlRunner.Patches.RecordPatches).GetMethod(
                        nameof(AlRunner.Patches.RecordPatches.NavRecord_TestFieldNotBlank),
                        BindingFlags.Public | BindingFlags.Static)!;
                    Hook(testFieldNotBlank, repl, "NavRecord.TestFieldNotBlank");
                }
                var testFieldError = navRecordType.GetMethod("TestFieldError",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { nclMetaFieldT, typeof(string), navAlErrorInfoT }, null);
                if (testFieldError != null)
                {
                    var repl = typeof(AlRunner.Patches.RecordPatches).GetMethod(
                        nameof(AlRunner.Patches.RecordPatches.NavRecord_TestFieldError),
                        BindingFlags.Public | BindingFlags.Static)!;
                    Hook(testFieldError, repl, "NavRecord.TestFieldError");
                }
            }
        }

        // NavRecord.GetCallerRecord(NavSession) — migrated to the Cecil layer (see
        // GetCallerRecordPatches.NavRecord_GetCallerRecord and its CecilOwned registration in
        // NclCecilRewrite.cs). It used to be unconditionally hooked to return null here, which
        // meant BC's nested-Validate "skip the xRec re-snapshot when the caller IS the record
        // already being validated" optimization could never fire — see #1781.

        // NavFile.GetTenantIds(NavSession) — internal static, NREs at session.Tenant
        // on the skeleton. Return (Guid.Empty, "STANDALONE") to align with the
        // runner-identity sentinels used by Database.SerialNumber/TenantId.
        var navFileTypeForTenants = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavFile");
        if (navFileTypeForTenants != null)
        {
            var getTenantIds = navFileTypeForTenants.GetMethod("GetTenantIds",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (getTenantIds != null && getTenantIds.GetParameters().Length == 1)
                Hook(getTenantIds, nameof(ReturnEmptyTenantIds_OneArg), "NavFile.GetTenantIds");
        }

        // NavRecordRef.get_Target / CheckIsOpenAllowed / IsOpenAllowed / ALOpen (all overloads)
        // are Cecil-owned (see NclCecilRewrite.cs, NavRecordRef cluster Batch 8).

        // NavStringValue.CompareTo(NavStringValue) — real impl uses NavCurrentThread.Session.Culture
        // (null on skeleton). Replace with ordinal Value comparison.
        var navStringValueType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavStringValue");
        if (navStringValueType != null)
        {
            var compareTo = navStringValueType.GetMethod("CompareTo",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { navStringValueType }, null);
            if (compareTo != null)
                Hook(compareTo, nameof(NavStringValue_CompareTo),
                    "NavStringValue.CompareTo(NavStringValue)");
        }

        // BitArrayHelpers.Equals is Cecil-owned (see NclCecilRewrite.cs).

        // NavHttpRequestMessage.get_Target — same shape as NavRecordRef. Construct
        // SharedNavHttpRequestMessage parented to skeleton container.
        var navHttpReqType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavHttpRequestMessage");
        if (navHttpReqType != null)
        {
            var targetGetter = navHttpReqType.GetProperty("Target",
                BindingFlags.NonPublic | BindingFlags.Instance)?.GetGetMethod(true);
            if (targetGetter != null)
                Hook(targetGetter, nameof(NavHttpRequestMessage_get_Target),
                    "NavHttpRequestMessage.get_Target");
        }

        // NavHttpResponseMessageBase.get_Target — same shape. Construct SharedNavHttpResponseMessage
        // parented to skeleton container. Ctor is safe (no HTTP infrastructure call).
        var navHttpRespType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavHttpResponseMessageBase");
        if (navHttpRespType != null)
        {
            var targetGetter = navHttpRespType.GetProperty("Target",
                BindingFlags.NonPublic | BindingFlags.Instance)?.GetGetMethod(true);
            if (targetGetter != null)
                Hook(targetGetter, nameof(NavHttpResponseMessageBase_get_Target),
                    "NavHttpResponseMessageBase.get_Target");
        }

        // NavHttpClient.get_Target — same Option-C shape. SharedNavHttpClient(ITreeSharedObjectContainer)
        // is safe (no CreateClient/HTTP infrastructure in that ctor).
        var navHttpClientType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavHttpClient");
        if (navHttpClientType != null)
        {
            var targetGetter = navHttpClientType.GetProperty("Target",
                BindingFlags.NonPublic | BindingFlags.Instance)?.GetGetMethod(true);
            if (targetGetter != null)
                Hook(targetGetter, nameof(NavHttpClient_get_Target), "NavHttpClient.get_Target");
        }

        // SharedNavHttpClient.CreateFactoryClient() — triggered by UseServerCertificateValidation
        // and other methods that need a real HTTP factory. Initialises a LazyEx<> that internally
        // calls AddHttpClient(IServiceCollection) → AddLogging(IServiceCollection), which crashes
        // via R2R PrestubMethodFrame on Linux in headless mode. Return null: the callers
        // (ALSend, ALUseTls etc.) are separately stubbed or no-op'd so null is safe.
        var sharedNavHttpClientType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.SharedNavHttpClient");
        if (sharedNavHttpClientType != null)
        {
            var createFactory = sharedNavHttpClientType.GetMethod("CreateFactoryClient",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (createFactory != null)
                Hook(createFactory, nameof(ReturnNull_OneArg), "SharedNavHttpClient.CreateFactoryClient");

            // UseServerCertificateValidation(bool) — sets SSL validation flag; no-op in headless.
            foreach (var m in sharedNavHttpClientType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (m.Name != "ALUseServerCertificateValidation" && m.Name != "UseServerCertificateValidation") continue;
                var ps = m.GetParameters().Length;
                var noop = ps switch { 1 => nameof(NoOp2), 2 => nameof(NoOp3), _ => null };
                if (noop != null) Hook(m, noop, $"SharedNavHttpClient.{m.Name}({ps})");
            }
        }

        // NavStream.get_Target is Cecil-owned (see NclCecilRewrite.cs).

        // NavSession.GetPermissionSet (both 3-arg overloads) is Cecil-owned (see
        // NclCecilRewrite.cs, Batch 8).

        // ALSystemNumeric.ALRandomize/ALRandom — real impls reach NavCurrentThread.Session.Random
        // (null on skeleton). Back with a process-static Random.
        var alSysNumType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ALSystemNumeric");
        if (alSysNumType != null)
        {
            var randomizeNoArg = alSysNumType.GetMethod("ALRandomize",
                BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (randomizeNoArg != null)
                Hook(randomizeNoArg, nameof(ALSystemNumeric_ALRandomize), "ALSystemNumeric.ALRandomize()");
            var randomizeSeed = alSysNumType.GetMethod("ALRandomize",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int) }, null);
            if (randomizeSeed != null)
                Hook(randomizeSeed, nameof(ALSystemNumeric_ALRandomize_Seed), "ALSystemNumeric.ALRandomize(int)");
            var alRandom = alSysNumType.GetMethod("ALRandom",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int) }, null);
            if (alRandom != null)
                Hook(alRandom, nameof(ALSystemNumeric_ALRandom), "ALSystemNumeric.ALRandom(int)");
        }

        // NavDialog.ALOpen — UI dialog open NREs reaching Tree.Session on skeleton. No-op.
        var navDialogType2 = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavDialog");
        if (navDialogType2 != null)
        {
            foreach (var m in navDialogType2.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "ALOpen" && m.GetParameters().Length == 3))
            {
                Hook(m, nameof(NavDialog_ALOpen), $"NavDialog.ALOpen/3");
            }
        }

        // ALSystemString.ALLowercase / ALUppercase — real impls reach Session.Culture (null
        // on skeleton). Fall back to InvariantCulture.
        var alSysStrType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ALSystemString");
        if (alSysStrType != null)
        {
            var lower = alSysStrType.GetMethod("ALLowercase",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (lower != null)
                Hook(lower, nameof(ALSystemString_ALLowercase), "ALSystemString.ALLowercase");
            var upper = alSysStrType.GetMethod("ALUppercase",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (upper != null)
                Hook(upper, nameof(ALSystemString_ALUppercase), "ALSystemString.ALUppercase");
        }

        // RecordImplementation.GetActiveCompany is Cecil-owned (see NclCecilRewrite.cs).

        // ── (A) Spike: async entry-point hook DISABLED
        //ApplyALFieldCaptionAsyncHook(navNcl);

        // ── Record / data-access plumbing (~300 lines) lives in RecordWritePatches.cs ──
        ApplyRecordPatches(navNcl);

        // FlowField CalcFields evaluator — directly evaluates Sum/Count/Exist/Min/Max/Lookup
        // against the in-memory TempTableDataProvider, bypassing the broken async pipeline.
        var ffNavTypesAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types");
        if (ffNavTypesAsm != null)
            AlRunner.Patches.FlowFieldPatches.Register(navNcl, ffNavTypesAsm);

        // NavCancellationToken throws — uninitialized cancellation tokens trip the check.
        var typesAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types");
        var ctType = typesAsm?.GetType("Microsoft.Dynamics.Nav.Types.NavCancellationToken");
        if (ctType != null)
        {
            foreach (var name in new[] { "ThrowOperationCanceledException", "ThrowIfCancellationRequested" })
            foreach (var m in ctType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                | BindingFlags.Instance | BindingFlags.Static)
                                    .Where(mm => mm.Name == name))
            {
                var p = m.GetParameters().Length + (m.IsStatic ? 0 : 1);
                var noop = p switch { 1 => nameof(NoOp_OneArg), 2 => nameof(NoOp2), _ => null };
                if (noop != null) Hook(m, noop, $"NavCancellationToken.{name}/{m.GetParameters().Length}");
            }
        }

        // NavSessionSettings.ALInit — called when AL SessionSettings variable is initialised;
        // NREs through NavGlobal / session infrastructure. No-op leaves settings at defaults.
        var sessSettingsType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavSessionSettings");
        if (sessSettingsType != null)
        {
            var alInit = sessSettingsType.GetMethod("ALInit",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (alInit != null)
                Hook(alInit, nameof(NoOp_OneArg), "NavSessionSettings.ALInit");
        }

        // NavCodeunit.ContainsMethod(int, string, object[]) — chains through NCLMetadata; return false.
        var navCodeunitType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavCodeunit");
        if (navCodeunitType != null)
        {
            var containsMethod = navCodeunitType.GetMethod("ContainsMethod",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (containsMethod != null)
                Hook(containsMethod, nameof(ReturnFalse_3Args), "NavCodeunit.ContainsMethod");
        }

        // NavDialog.ALError(NavSession, Guid, NavALErrorInfo) — NREs when accessing diagnostics on
        // the skeleton session. Throw NavNCLDialogException so asserterror traps it correctly.
        var navDialogType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavDialog");
        if (navDialogType != null && typesAsm != null)
        {
            _navNCLDialogExceptionType = typesAsm.GetType(
                "Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLDialogException");
            foreach (var m in navDialogType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(m => m.Name == "ALError"))
            {
                var ps = m.GetParameters();
                // Only hook overloads that take NavALErrorInfo as the last param.
                if (ps.Length < 1 || ps[ps.Length - 1].ParameterType.Name != "NavALErrorInfo")
                    continue;
                // Guid (16 bytes) occupies 2 x64 register slots on Linux .NET 8.
                // 2-arg (Guid, NavALErrorInfo):          slots = Guid-lo, Guid-hi, errorInfo  → 3 params ✓
                // 3-arg (NavSession, Guid, NavALErrorInfo): slots = session, Guid-lo, Guid-hi, errorInfo → 4 params
                //   This 3-arg overload is only called from ALLogInternalError (Internal-type errors),
                //   which we already no-op; no-op the overload itself too as belt-and-suspenders.
                bool hasSession = ps.Length >= 2 && ps[0].ParameterType.Name == "NavSession";
                var replacementName = hasSession ? nameof(NoOp4) : nameof(NavDialogALError_NavALErrorInfo);
                Hook(m, replacementName, $"NavDialog.ALError/{ps.Length}");
            }
            // NavDialog.ALLogInternalError — calls ALError internally; no-op so Dialog.LogInternalError
            // behaves like a trace (matching existing AL Runner behavior). All static overloads.
            foreach (var m in navDialogType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(m => m.Name == "ALLogInternalError"))
            {
                var np = m.GetParameters().Length;
                var noop = np switch { 3 => nameof(NoOp3), 4 => nameof(NoOp4), 5 => nameof(NoOp5), _ => null };
                if (noop != null) Hook(m, noop, $"NavDialog.ALLogInternalError/{np}");
            }
        }

        // NavALErrorInfo.LogAddActionFailure(string) — private static telemetry; no-op.
        var navALErrorInfoType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavALErrorInfo");
        if (navALErrorInfoType != null)
        {
            var logFail = navALErrorInfoType.GetMethod("LogAddActionFailure",
                BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (logFail != null)
                Hook(logFail, nameof(NoOp_OneArg), "NavALErrorInfo.LogAddActionFailure(string)");
        }

        // ALSession.GetALCurrentClientType(NavSession) — switches on session.ClientConnectionType
        // which NREs on the skeleton session. Return Background as a safe default.
        // ALSession.ALStopSessionAsync — async stop-session; returns ValueTask<bool>(false).
        var alSessionType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ALSession");
        if (alSessionType != null && sessType != null)
        {
            var getClientType = alSessionType.GetMethod("GetALCurrentClientType",
                BindingFlags.Public | BindingFlags.Static, null, new[] { sessType }, null);
            if (getClientType != null)
                Hook(getClientType, nameof(ALSession_GetALCurrentClientType), "ALSession.GetALCurrentClientType");

            // Hook all ALStopSessionAsync overloads — they all NRE via session.Diagnostics on skeleton.
            // Also hook the sync ALStopSession wrappers as belt-and-suspenders (they call Async internally).
            foreach (var m in alSessionType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "ALStopSessionAsync"))
            {
                Hook(m, nameof(ALSession_StopSessionAsync), $"ALSession.ALStopSessionAsync/{m.GetParameters().Length}");
            }
            foreach (var m in alSessionType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "ALStopSession"))
            {
                var np = m.GetParameters().Length;
                var repl = np switch { 2 => nameof(ReturnFalse_2Args), 3 => nameof(ReturnFalse_3Args), _ => null };
                if (repl != null) Hook(m, repl, $"ALSession.ALStopSession/{np}");
            }
        }

        // NavCodeunit.DoRunAsync and NavCodeunit.RunCodeunit are Cecil-owned (see
        // NclCecilRewrite.cs, "NavCodeunit run path" Batch 8).

        // NavMethodScope.ProcessException(Exception) is Cecil-owned (see NclCecilRewrite.cs,
        // "NavMethodScope cluster").

        // ALDebugger — all methods are obsolete stubs; handled at source level via BcAssembler
        // polyfill redirects to avoid ABI issues with value-type parameters (DataError enum).

        // NavApplicationObjectBase.TryInvoke(NavSession, Action) is Cecil-owned (see
        // NclCecilRewrite.cs).
        // ALSession.ALEnableVerboseTelemetry — telemetry enable/disable; no-op is safe.
        if (alSessionType != null)
        {
            foreach (var m in alSessionType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "ALEnableVerboseTelemetry"))
            {
                var np = m.GetParameters().Length;
                var noop = np switch { 1 => nameof(NoOp_OneArg), 2 => nameof(NoOp2), 3 => nameof(NoOp3), 4 => nameof(NoOp4), _ => null };
                if (noop != null) Hook(m, noop, $"ALSession.ALEnableVerboseTelemetry/{np}");
            }
        }

        // ALNavApp.ALNavAppIsInstalling() — static, returns bool; no install in progress → false.
        var alNavAppType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.ALNavApp");
        if (alNavAppType != null)
        {
            var isInstalling = alNavAppType.GetMethod("ALNavAppIsInstalling",
                BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (isInstalling != null)
                Hook(isInstalling, nameof(ReturnFalse_0Args), "ALNavApp.ALNavAppIsInstalling");
        }

        // NavSessionSettings.ALRequestSessionUpdate(bool) — no-op; no live session to update.
        if (sessSettingsType != null)
        {
            var reqUpdate = sessSettingsType.GetMethod("ALRequestSessionUpdate",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (reqUpdate != null)
                Hook(reqUpdate, nameof(NoOp2), "NavSessionSettings.ALRequestSessionUpdate");
        }

        // CallStackElement.TryGetSourceInfo(out ObjectSourceInfo) — chains through NavGlobal.NCLMetadata
        // which NREs on the skeleton session. Return false (no source info available) and set the
        // out-param pointer to zero so callers see a null/default sourceInfo.
        var callStackElemType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.CallStackElement");
        if (callStackElemType != null)
        {
            var tryGetSrc = callStackElemType.GetMethod("TryGetSourceInfo",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (tryGetSrc != null)
                Hook(tryGetSrc, nameof(CallStackElement_TryGetSourceInfo),
                    "CallStackElement.TryGetSourceInfo");
        }

        // NCLMetaApplicationObject.get_ApplicationObjectConstructor is Cecil-owned (see
        // NclCecilRewrite.cs, Batch 7).

        // DISABLED: ApplyInstallIndirectSpike — Step 3 calls InstallIndirect (PrepareMethod) on
        // get_IsLocalLanguage after JmpHook.Apply has already patched its precode. PrepareMethod
        // on an already-patched method SIGSEGVs because the 14-byte JMP overwrite corrupts the
        // precode bytes the runtime reads during re-preparation. Step 4 was already disabled.
        // The spike proved the InstallIndirect cell-patch mechanism is structurally sound;
        // disabling the call loses zero runtime functionality. See issue #1619 for follow-up.
        // ApplyInstallIndirectSpike(navNcl);

        // NavMediaSet (ALInsert/ALRemove/get_ALCount/ALItem/ALImport/ALExport/get_ALMediaId) and
        // NavNotification.ALSend/ALRecall are Cecil-owned (see NclCecilRewrite.cs).

        // NavDialog.ALStrMenu (all overloads) and NavDialog.ALConfirm (all overloads) are
        // Cecil-owned (see NclCecilRewrite.cs, Batch 5 — inline const/arg body rewrite).
    }

    // ── InstallIndirect spike implementation ────────────────────────────────────────────────

    internal static void ApplyInstallIndirectSpike(Assembly navNcl)
    {
        Console.Error.WriteLine("[IndirectSpike] === BEGIN ApplyInstallIndirectSpike ===");
        try
        {
            // Step 3: sync re-hook — NavSession.get_IsLocalLanguage (already hooked by Apply above).
            // We call InstallIndirect on it a second time, pointing to the same replacement.
            // The cell is already pointing to our first hook, so this is a no-op functionally,
            // but it exercises the cell-locate / mprotect / write path on a known-safe method.
            var sessType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavSession");
            if (sessType != null)
            {
                var isLocalLang = sessType.GetProperty("IsLocalLanguage",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetGetMethod(true);
                if (isLocalLang != null)
                {
                    var repl = typeof(BcRuntime).GetMethod(nameof(ReturnFalse_1Arg),
                        BindingFlags.Public | BindingFlags.Static)
                        ?? throw new InvalidOperationException("ReturnFalse_1Arg not found");
                    bool ok = JmpHook.InstallIndirect(isLocalLang, repl,
                        "SPIKE-Step3: NavSession.get_IsLocalLanguage (sync re-hook)");
                    Console.Error.WriteLine($"[IndirectSpike] Step 3 (sync re-hook): {(ok ? "GREEN" : "RED — precode shape mismatch")}");
                }
                else
                {
                    Console.Error.WriteLine("[IndirectSpike] Step 3: IsLocalLanguage not found — skipped");
                }
            }
            else
            {
                Console.Error.WriteLine("[IndirectSpike] Step 3: NavSession type not found — skipped");
            }
        }
        catch (Exception ex3)
        {
            Console.Error.WriteLine($"[IndirectSpike] Step 3 THREW: {ex3.GetType().FullName}: {ex3.Message}");
        }

        try
        {
            // Step 4: async entry-point hook — NavRecord.ALFieldCaptionAsync(int).
            // Previous spike crashed (14-byte overwrite corrupted MOV R10 at bytes 6-12).
            // Cell-patch leaves bytes 6-12 intact — the MethodDesc stays readable for lazy JIT.
            //
            // Step 4: async entry-point hook — NavRecord.ALFieldCaptionAsync(int).
            // DISABLED — cell-patch installs without crash but SIGSEGV occurs during test
            // execution before the replacement is ever called. The crash is in JIT compilation
            // of a new caller that reads the patched cell. See spike report for full analysis.
            // The cell-patch mechanism itself is proven correct (Step 3 GREEN).
            // Async methods require a DIFFERENT dispatch strategy (see report).
            Console.Error.WriteLine("[IndirectSpike] Step 4: DISABLED — see spike report");
        }
        catch (Exception ex4)
        {
            Console.Error.WriteLine($"[IndirectSpike] Step 4 THREW: {ex4.GetType().FullName}: {ex4.Message}");
        }

        Console.Error.WriteLine("[IndirectSpike] === END ApplyInstallIndirectSpike ===");
    }

    private static void HookProperty(Type t, string propName, bool isStatic, string replacementName)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic |
                    (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        var p = t.GetProperty(propName, flags);
        if (p?.GetMethod != null) Hook(p.GetMethod, replacementName, $"{t.Name}.get_{propName}");
    }

    private static void HookMethodIfExists(Type t, string methodName, BindingFlags flags,
                                           Func<MethodInfo, string?> picker)
    {
        var m = t.GetMethod(methodName, flags);
        if (m == null) return;
        var name = picker(m);
        if (name != null) Hook(m, name, $"{t.Name}.{methodName}");
    }

    private static void Hook(MethodBase original, string replacementName, string description)
    {
        var repl = typeof(BcRuntime).GetMethod(replacementName,
            BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Replacement {replacementName} not found");
        JmpHook.Apply(original, repl, description);
    }

    private static void Hook(MethodBase original, MethodInfo replacement, string description)
        => JmpHook.Apply(original, replacement, description);
}
