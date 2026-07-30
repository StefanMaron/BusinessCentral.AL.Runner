// InstallTriggerRunner — fires each loaded app's Subtype=Install codeunit
// lifecycle triggers (OnInstallAppPerCompany / OnInstallAppPerDatabase),
// modelling a freshly-installed app before the bundle's tests run.
//
// Real BC (Ncl: NavAppInstallationProcessor.RaiseEventsForTransactionalPhase)
// raises the system Extension Triggers events (Codeunit 2000000010, method ids
// 1035466569 / 757022013) per installing app — per-company first, then
// per-database — and the app's Subtype=Install codeunit triggers are the
// compiled subscribers that receive them. The runner reaches the exact same
// ISV/MS trigger bodies by invoking the emitted public trigger methods
// directly on an instantiated codeunit (the same ITreeObject-ctor +
// MethodInfo.Invoke dispatch TestExecutor/RunFirstCodeunitOnRun use), so the
// real AL body runs against the in-memory table provider + skeleton session.
//
// Persistence semantics: on real BC, install-seeded data is committed and
// survives the per-test rollback — every test starts from a baseline that
// INCLUDES the install seed. The runner's per-test/per-codeunit reset wipes
// the whole in-memory store instead of rolling back to a commit point, so the
// faithful equivalent is to re-fire the install triggers after every reset
// (TestExecutor calls RunAll() right after RecordPatches.ResetPerTestState()).
// Scanning is cached per assembly; re-firing is a no-op-cost loop for the
// overwhelmingly common case of bundles with no Install codeunit.
//
// Loud failures (.claude/rules/loud-failures.md): a throwing install trigger
// is rethrown with its ORIGINAL exception type preserved (so a
// RunnerOutOfScopeException surfaces as such), after a stderr line naming the
// codeunit + trigger. Never swallowed.
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace AlRunner;

public static class InstallTriggerRunner
{
    private sealed record InstallCodeunit(
        Type Type, ConstructorInfo Ctor, MethodInfo? PerCompany, MethodInfo? PerDatabase);

    // Dependency-app assemblies in dependency order (a dep's Install fires
    // before its dependent's), then the bundle's own test assembly last.
    private static readonly List<Assembly> _depAssemblies = new();
    private static Assembly? _testAssembly;

    // Scan cache — an assembly's set of Install codeunits never changes.
    private static readonly Dictionary<Assembly, IReadOnlyList<InstallCodeunit>> _scanCache = new();

    /// <summary>Forget the previous bundle's assemblies (called at the start of
    /// each bundle iteration so a dep-less bundle doesn't inherit stale deps).</summary>
    public static void ResetForNewBundle()
    {
        lock (_depAssemblies)
        {
            _depAssemblies.Clear();
            _testAssembly = null;
        }
    }

    /// <summary>Register the bundle's dependency assemblies, in dependency order
    /// (DependencyLoader.LoadAll already returns them resolved dep-first).</summary>
    public static void SetDependencyAssemblies(IEnumerable<Assembly> assemblies)
    {
        lock (_depAssemblies)
        {
            _depAssemblies.Clear();
            _depAssemblies.AddRange(assemblies);
        }
    }

    /// <summary>Register the bundle's own (test) assembly — its Install codeunits
    /// fire after all dependency apps', matching install order.</summary>
    public static void SetTestAssembly(Assembly assembly)
    {
        lock (_depAssemblies)
            _testAssembly = assembly;
    }

    /// <summary>Fire every registered app's Install triggers once, dep order,
    /// per-company then per-database within each app (the order BC's
    /// NavAppInstallationProcessor raises them for an installing app).</summary>
    public static void RunAll()
    {
        List<Assembly> ordered;
        lock (_depAssemblies)
        {
            ordered = new List<Assembly>(_depAssemblies);
            if (_testAssembly != null && !ordered.Contains(_testAssembly))
                ordered.Add(_testAssembly);
        }
        foreach (var asm in ordered)
            foreach (var cu in Scan(asm))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var instance = cu.Ctor.Invoke(new object[] { BcRuntime.RootTreeStub! });
                try
                {
                    InvokeTrigger(cu, instance, cu.PerCompany, "OnInstallAppPerCompany");
                    InvokeTrigger(cu, instance, cu.PerDatabase, "OnInstallAppPerDatabase");
                }
                finally
                {
                    // MEMORY LEAK FIX: this instance was constructed parented to the
                    // process-wide BcRuntime.RootTreeStub (ITreeObject ctor →
                    // TreeHandler.CreateTreeHandler → parentHandler.InternalAddChild),
                    // which permanently links it into RootTreeStub's child chain unless
                    // disposed. RunAll() re-instantiates every Install codeunit on every
                    // call — once per test codeunit under the default Codeunit isolation,
                    // once per TEST under Test isolation (see TestExecutor.cs) — so without
                    // disposal this is the dominant amplifier of the runner's memory growth
                    // across a full corpus run. The instance is purely transient (models a
                    // one-shot install-trigger firing; any seeded state lives in the
                    // in-memory table store, not on this object), so disposing it
                    // immediately after its triggers fire is faithful and unlinks it from
                    // RootTreeStub via TreeHandler.Dispose()'s InternalRemoveChild.
                    (instance as IDisposable)?.Dispose();
                }
                PerfTrace.Log($"InstallTrigger {cu.Type.Name} ({asm.GetName().Name}) {sw.ElapsedMilliseconds}ms");
            }
    }

    private static void InvokeTrigger(InstallCodeunit cu, object instance, MethodInfo? trigger, string name)
    {
        if (trigger == null) return;
        try
        {
            trigger.Invoke(instance, null);
        }
        catch (TargetInvocationException tex) when (tex.InnerException != null)
        {
            // Loud, type-preserving: name the failing surface, then rethrow the
            // ORIGINAL exception (RunnerOutOfScopeException stays itself).
            Console.Error.WriteLine(
                $"[install-trigger] {cu.Type.Name}.{name} ({cu.Type.Assembly.GetName().Name}) threw: " +
                $"{tex.InnerException.GetType().Name}: {tex.InnerException.Message}");
            var alStack = AlRunner.Infrastructure.AlCallStackCapture.GetCaptured(tex.InnerException);
            if (!string.IsNullOrEmpty(alStack))
                Console.Error.WriteLine($"[install-trigger] AL stack:\n{alStack}");
            else
                Console.Error.WriteLine($"[install-trigger] {tex.InnerException}");
            ExceptionDispatchInfo.Capture(tex.InnerException).Throw();
        }
    }

    private static IReadOnlyList<InstallCodeunit> Scan(Assembly asm)
    {
        lock (_scanCache)
        {
            if (_scanCache.TryGetValue(asm, out var cached)) return cached;
        }

        Type?[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types; }

        var found = new List<InstallCodeunit>();
        foreach (var t in types)
        {
            if (t == null || !t.Name.StartsWith("Codeunit", StringComparison.Ordinal)) continue;
            var perCompany = InstallTriggerMethod(t, "OnInstallAppPerCompany");
            var perDatabase = InstallTriggerMethod(t, "OnInstallAppPerDatabase");
            if (perCompany == null && perDatabase == null) continue;
            var ctor = t.GetConstructors().FirstOrDefault(c =>
                c.GetParameters().Length == 1 &&
                c.GetParameters()[0].ParameterType.Name == "ITreeObject");
            if (ctor == null) continue;
            found.Add(new InstallCodeunit(t, ctor, perCompany, perDatabase));
        }

        IReadOnlyList<InstallCodeunit> result = found;
        lock (_scanCache)
            _scanCache[asm] = result;
        return result;
    }

    /// <summary>
    /// A Subtype=Install codeunit's lifecycle trigger compiles to a public
    /// parameterless method carrying [NavEventSubscriber] targeting the system
    /// "Extension Triggers" codeunit 2000000010's matching install event (that
    /// is how BC's NavAppInstallationProcessor reaches it: it raises the system
    /// event and the install codeunit's trigger is the compiled subscriber).
    /// Matching on that attribute — NOT on the method name alone — is what
    /// scopes this step to real Install triggers: a look-alike procedure on a
    /// Normal codeunit has no such subscriber attribute. Works identically for
    /// our own emit output and for MS/ISV-precompiled app DLLs.
    /// (Note: the class-level [NavCodeunitOptions] Subtype can NOT be used —
    /// our emit pipeline stamps Normal there even for Install codeunits.)
    /// </summary>
    private static MethodInfo? InstallTriggerMethod(Type t, string name)
    {
        var m = t.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (m == null) return null;
        object[] attrs;
        try { attrs = m.GetCustomAttributes(inherit: false); }
        catch { return null; }
        foreach (var a in attrs)
        {
            if (a.GetType().Name != "NavEventSubscriberAttribute") continue;
            var at = a.GetType();
            if ((string?)at.GetProperty("TargetMethodName")?.GetValue(a) != name) continue;
            var oid = at.GetProperty("TargetObjectId")?.GetValue(a);
            var objNo = oid?.GetType().GetProperty("ObjectNumber")?.GetValue(oid) as int?;
            if (objNo == ExtensionTriggersCodeunitId) return m;
        }
        return null;
    }

    /// <summary>System codeunit 2000000010 "Extension Triggers" — the publisher
    /// of OnInstallAppPerDatabase / OnInstallAppPerCompany.</summary>
    private const int ExtensionTriggersCodeunitId = 2000000010;
}
