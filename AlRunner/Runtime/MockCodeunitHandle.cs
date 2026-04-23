namespace AlRunner.Runtime;

using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// Routes cross-codeunit method calls to generated codeunit classes.
/// Replaces NavCodeunitHandle for standalone execution.
/// </summary>
public class MockCodeunitHandle
{
    private readonly int _codeunitId;
    private object? _codeunitInstance;

    /// <summary>
    /// The assembly containing compiled codeunit classes. Set before execution.
    /// </summary>
    public static Assembly? CurrentAssembly { get; set; }

    /// <summary>
    /// Set of auto-stubbed codeunit IDs that were accessed during the current test.
    /// Shared across threads (concurrent). Reset before each test.
    /// Used to produce actionable timeout messages.
    /// </summary>
    public static System.Collections.Concurrent.ConcurrentBag<int>? AccessedAutoStubs;

    /// <summary>Reset the per-test auto-stub tracking.</summary>
    public static void ResetAutoStubTracking() => AccessedAutoStubs = new();


    /// <summary>
    /// Additional assemblies containing compiled dependency codeunits/tables/pages.
    /// Loaded from --dep-dlls directories. Searched by FindCodeunitType, event dispatch,
    /// and record trigger resolution when a type is not found in CurrentAssembly.
    /// </summary>
    public static List<Assembly>? DependencyAssemblies { get; set; }

    /// <summary>
    /// Cache for SingleInstance codeunits — one instance per ID across the session.
    /// In BC, SingleInstance=true means the same codeunit instance is shared.
    /// Reset between test codeunits via <see cref="ResetSingleInstances"/>.
    /// </summary>
    private static readonly Dictionary<int, object> _singleInstances = new();
    public static void ResetSingleInstances() => _singleInstances.Clear();

    /// <summary>Get a cached SingleInstance, or null.</summary>
    public static object? GetSingleInstance(Type codeunitType)
    {
        if (codeunitType.Name.StartsWith("Codeunit") &&
            int.TryParse(codeunitType.Name.AsSpan(8), out var id) &&
            _singleInstances.TryGetValue(id, out var inst))
            return inst;
        return null;
    }

    /// <summary>Cache a codeunit instance if its type has IsSingleInstance=true.</summary>
    public static void CacheSingleInstanceIfNeeded(Type codeunitType, object instance)
    {
        if (!IsSingleInstanceType(codeunitType)) return;
        if (codeunitType.Name.StartsWith("Codeunit") &&
            int.TryParse(codeunitType.Name.AsSpan(8), out var id))
            _singleInstances[id] = instance;
    }

    private static bool IsSingleInstanceType(Type t)
    {
        var prop = t.GetProperty("IsSingleInstance",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        if (prop != null) try { return (bool)prop.GetValue(null)!; } catch { }
        return false;
    }

    public MockCodeunitHandle(int codeunitId)
    {
        _codeunitId = codeunitId;
    }

    /// <summary>
    /// 2-arg constructor: (parent, codeunitId).
    /// BC emits this form for codeunit return values (e.g. exit(this) / fluent-builder pattern).
    /// The parent object is unused in standalone mode.
    /// </summary>
    public MockCodeunitHandle(object? parent, int codeunitId)
    {
        _codeunitId = codeunitId;
    }

    /// <summary>
    /// Resets the handle (no-op for standalone execution).
    /// Called from OnClear() in generated codeunit classes.
    /// </summary>
    public void Clear()
    {
        _codeunitInstance = null;
    }

    /// <summary>
    /// Clears the reference (no-op for standalone execution).
    /// Called from generated codeunit code when resetting handles.
    /// </summary>
    public void ClearReference()
    {
        _codeunitInstance = null;
    }

    /// <summary>
    /// AL assignment: CodeunitA := CodeunitB.
    /// Copies the codeunit reference so both variables point to the same instance.
    /// </summary>
    public void ALAssign(MockCodeunitHandle other)
    {
        _codeunitInstance = other._codeunitInstance;
    }

    /// <summary>
    /// Bind this codeunit as a manual event subscriber. The underlying
    /// generated class instance is created (if needed) and registered so
    /// its subscriber methods fire during event dispatch.
    /// </summary>
    public void Bind()
    {
        var assembly = CurrentAssembly ?? Assembly.GetExecutingAssembly();
        var codeunitType = FindCodeunitType(assembly);
        if (codeunitType == null) return;
        EnsureInstance(codeunitType);
        if (_codeunitInstance != null)
            EventSubscriberRegistry.Bind(_codeunitInstance);
    }

    /// <summary>
    /// Unbind a previously bound manual event subscriber instance.
    /// </summary>
    public void Unbind()
    {
        if (_codeunitInstance != null)
            EventSubscriberRegistry.Unbind(_codeunitInstance);
    }

    /// <summary>
    /// Lazily create the codeunit class instance and run InitializeComponent.
    /// For SingleInstance codeunits, returns the shared instance from the cache.
    /// </summary>
    private void EnsureInstance(Type codeunitType)
    {
        if (_codeunitInstance != null) return;
        // SingleInstance: reuse cached instance
        if (_singleInstances.TryGetValue(_codeunitId, out var cached))
        {
            _codeunitInstance = cached;
            return;
        }
        _codeunitInstance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(codeunitType);
        var initMethod = codeunitType.GetMethod("InitializeComponent",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        initMethod?.Invoke(_codeunitInstance, null);
        // Cache SingleInstance codeunits
        if (IsSingleInstanceType(codeunitType))
            _singleInstances[_codeunitId] = _codeunitInstance;
    }

    /// <summary>
    /// Returns the underlying codeunit instance, lazily creating it if needed.
    /// Used by MockInterfaceHandle.IsInterfaceOfType to call the BC-generated
    /// IsInterfaceOfType(int) method on the codeunit class.
    /// </summary>
    public object? GetUnderlyingInstance()
    {
        if (_codeunitInstance != null) return _codeunitInstance;
        var assembly = CurrentAssembly ?? Assembly.GetExecutingAssembly();
        var codeunitType = FindCodeunitType(assembly);
        if (codeunitType == null) return null;
        EnsureInstance(codeunitType);
        return _codeunitInstance;
    }

    /// <summary>
    /// Static factory matching the rewritten constructor pattern.
    /// </summary>
    public static MockCodeunitHandle Create(int codeunitId)
    {
        return new MockCodeunitHandle(codeunitId);
    }

    /// <summary>
    /// Creates a MockCodeunitHandle that wraps an existing codeunit instance.
    /// Used for exit(this) in fluent-builder codeunits: the BC compiler emits
    /// __ThisHandle which references the codeunit itself as a NavCodeunitHandle.
    /// The codeunit ID is extracted from the class name (e.g. "Codeunit50100" -> 50100).
    /// </summary>
    public static MockCodeunitHandle FromInstance(object instance)
    {
        // Extract codeunit ID from class name "CodeunitNNNNN"
        var typeName = instance.GetType().Name;
        int id = 0;
        if (typeName.StartsWith("Codeunit"))
        {
            int.TryParse(typeName.Substring("Codeunit".Length), out id);
        }
        var handle = new MockCodeunitHandle(id);
        handle._codeunitInstance = instance;
        return handle;
    }

    /// <summary>
    /// Invoke a method by its member ID. The generated codeunit has public methods
    /// like ApplyDiscount(...) that create scope objects internally.
    /// We find the matching public method by looking at the scope class name which
    /// encodes the member ID.
    /// </summary>
    public object? Invoke(int memberId, object[] args)
    {
        // Route codeunit 130 ("Library Assert"), 131 ("Assert" alias stub), 130000 (Assert from BC test toolkit), and 130002 (real BC "Library Assert" ID) to MockAssert
        if (_codeunitId is 130 or 131 or 130000 or 130002)
            return InvokeAssert(memberId, args);

        // Route codeunit 131004 (Library - Variable Storage) to MockVariableStorage
        if (_codeunitId is 131004)
            return InvokeVariableStorage(memberId, args);

        // Route codeunit 131100 (AL Runner Config) — exposes CompanyName configuration to AL
        if (_codeunitId is 131100)
            return InvokeRunnerConfig(memberId, args);

        // Track codeunit access for timeout diagnostics
        try { AccessedAutoStubs?.Add(_codeunitId); } catch { }



        var assembly = CurrentAssembly ?? Assembly.GetExecutingAssembly();
        var codeunitType = FindCodeunitType(assembly);
        if (codeunitType == null)
        {
            // System codeunits (IDs 1-9999) are part of the BC platform and cannot be provided
            // as user stubs. Treat calls to missing system codeunits as no-ops instead of
            // throwing: the subscriber dispatch path works independently of the publisher class.
            if (IsSystemCodeunitId(_codeunitId))
                return null;

            throw new InvalidOperationException(BuildCodeunitNotFoundMessage(_codeunitId, assembly));
        }

        EnsureInstance(codeunitType);

        // Find the public method whose scope class name contains the memberId.
        // Scope classes are named like: ApplyDiscount_Scope_1351223168
        // The memberId passed to Invoke matches the number in the scope name.
        // We look for a nested scope type matching the memberId, then call the
        // parent public method that creates that scope.
        var absMemberId = Math.Abs(memberId).ToString();
        var memberIdStr = memberId.ToString();

        // Strategy: find the nested scope class whose name ends with the memberId,
        // then find the public method on the codeunit that references it.
        foreach (var nested in codeunitType.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
        {
            // Scope names look like: MethodName_Scope_NNNNN or MethodName_Scope__NNNNN (negative)
            if (nested.Name.Contains($"_Scope_{memberIdStr}") ||
                nested.Name.Contains($"_Scope__{absMemberId}"))
            {
                // Extract the method name from the scope class name
                // e.g. "ApplyDiscount_Scope_1351223168" -> "ApplyDiscount"
                var scopeName = nested.Name;
                var scopeIdx = scopeName.IndexOf("_Scope_");
                if (scopeIdx < 0) continue;
                var methodName = scopeName.Substring(0, scopeIdx);

                // Find the public method on the codeunit class.
                // For overloaded AL procedures, the BC compiler emits C# methods with the
                // member ID appended: "ProcessJson" (first) and "ProcessJson_2101255952"
                // (second). The scope class is "ProcessJson_Scope_2101255952" for both,
                // so methodName extracted above is always the base name "ProcessJson".
                // Try the exact name first; if not found or ambiguous, try the suffixed
                // variant "MethodName_MemberId" that the BC compiler uses for overloads.
                var suffixedName = $"{methodName}_{absMemberId}";
                var method = codeunitType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == suffixedName);
                if (method == null)
                {
                    // No suffixed overload — find by base name, matching arg count
                    var candidates = codeunitType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Where(m => m.Name == methodName)
                        .ToArray();
                    if (candidates.Length == 1)
                    {
                        method = candidates[0];
                    }
                    else if (candidates.Length > 1)
                    {
                        // Multiple overloads with the same base name: pick by arg count + type score
                        method = candidates
                            .Where(m => m.GetParameters().Length == args.Length)
                            .OrderByDescending(m => ScoreMethodMatch(m, args))
                            .FirstOrDefault()
                            ?? candidates.FirstOrDefault();
                    }
                }
                if (method == null) continue;

                // Convert args to match parameter types
                var parameters = method.GetParameters();
                var convertedArgs = new object?[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (i < args.Length)
                    {
                        convertedArgs[i] = ConvertArgInternal(args[i], parameters[i].ParameterType);
                    }
                }

                return method.Invoke(_codeunitInstance, convertedArgs);
            }
        }

        // Fallback: no exact member ID match. Try matching by method name (extracted
        // from the calling scope class) + argument count. This handles auto-stubbed
        // codeunits where member IDs don't match but method names do.
        string? callerMethodName = null;
        var stackFrames = new System.Diagnostics.StackTrace().GetFrames();
        if (stackFrames != null)
        {
            foreach (var frame in stackFrames)
            {
                var callerType = frame.GetMethod()?.DeclaringType;
                if (callerType == null) continue;
                var scopeIdx = callerType.Name.IndexOf("_Scope_");
                if (scopeIdx > 0)
                {
                    callerMethodName = callerType.Name.Substring(0, scopeIdx);
                    break;
                }
            }
        }

        var candidateMethods = codeunitType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetParameters().Length == args.Length && !m.IsSpecialName)
            .ToList();

        // If we know the method name from the caller's scope, prefer exact name match
        if (callerMethodName != null && candidateMethods.Count > 1)
        {
            var nameMatches = candidateMethods
                .Where(m => m.Name == callerMethodName)
                .ToList();
            if (nameMatches.Count > 0)
                candidateMethods = nameMatches;
        }

        if (candidateMethods.Count > 0)
        {
            // Sort by match quality. Try each candidate — if the caller gets a cast
            // error from the return type, fall through to the next candidate.
            var sorted = candidateMethods
                .OrderByDescending(m => ScoreMethodMatch(m, args))
                .ToList();
            for (int ci = 0; ci < sorted.Count; ci++)
            {
                var candidate = sorted[ci];
                var parameters = candidate.GetParameters();
                var convertedArgs = new object?[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (i < args.Length)
                        convertedArgs[i] = ConvertArgInternal(args[i], parameters[i].ParameterType);
                }
                try
                {
                    return candidate.Invoke(_codeunitInstance, convertedArgs);
                }
                catch (System.Reflection.TargetInvocationException) when (ci < sorted.Count - 1)
                {
                    continue; // try next candidate
                }
            }
        }

        // No matching method found. For auto-stubbed dependency objects (system
        // codeunits, base app, etc.) this is expected — the stub class exists but
        // has no compiled methods. Return null as a default no-op; the caller's
        // generated code handles null returns via default(T) conversions.
        return null;
    }

    /// <summary>
    /// Instance method: run the codeunit's OnRun trigger.
    /// Replacement for NavCodeunitHandle.Target.Run(DataError, record).
    /// In BC, this runs the codeunit passing a record parameter.
    ///
    /// Unlike the static RunCodeunit, this fires OnRun on the HANDLE'S instance
    /// (created on first call and reused afterward) so state mutations made by
    /// OnRun are visible to subsequent method calls on the same handle — matching
    /// AL's observed semantics when a codeunit variable is used as a mutable bag.
    /// </summary>
    public bool Run(DataError errorLevel, object? record = null)
    {
        try
        {
            var assembly = CurrentAssembly ?? Assembly.GetExecutingAssembly();
            var codeunitType = FindCodeunitType(assembly);
            if (codeunitType == null)
            {
                // Missing codeunits are treated as no-ops — fire OnRun event for
                // any subscribers but don't throw. This handles system codeunits
                // (1-9999), test toolkit (130000+), and any other dependency
                // codeunit that wasn't compiled or auto-stubbed.
                AlCompat.FireEvent(EventSubscriberRegistry.ObjectTypeCodeunit, _codeunitId, "OnRun");
                return true;
            }

            EnsureInstance(codeunitType);
            InvokeOnRun(codeunitType, _codeunitInstance!, record as MockRecordHandle);
            return true;
        }
        catch
        {
            if (errorLevel == DataError.TrapError) return false;
            throw;
        }
    }

    /// <summary>
    /// Shared OnRun dispatch: look up OnRun (parameterless or with record parameter)
    /// via reflection and invoke it on the provided instance. Kept as a helper so
    /// both Run (instance, state-preserving) and RunCodeunitCore (static, ephemeral)
    /// share the same trigger-lookup logic.
    /// </summary>
    private static void InvokeOnRun(Type codeunitType, object instance, MockRecordHandle? record)
    {
        var bindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var onRunMethod = codeunitType.GetMethod("OnRun", bindingFlags, null, Type.EmptyTypes, null);
        if (onRunMethod != null)
        {
            onRunMethod.Invoke(instance, null);
            return;
        }
        var onRunWithRecord = codeunitType.GetMethod("OnRun",
            bindingFlags, null, new[] { typeof(MockRecordHandle) }, null);
        if (onRunWithRecord != null)
        {
            onRunWithRecord.Invoke(instance, new object?[] { record });
            return;
        }
        // No OnRun → silently do nothing (matches BC behaviour for codeunits with no trigger).
    }

    /// <summary>
    /// Static dispatch: run a codeunit's OnRun trigger by ID.
    /// Replacement for NavCodeunit.RunCodeunit(DataError, codeunitId, record).
    /// When errorLevel is TrapError, exceptions are caught and false is returned.
    /// When errorLevel is ThrowError, exceptions propagate and true is returned on success.
    /// </summary>
    public static bool RunCodeunit(DataError errorLevel, int codeunitId, MockRecordHandle? record = null)
    {
        try
        {
            RunCodeunitCore(codeunitId, record);
            return true;
        }
        catch
        {
            if (errorLevel == DataError.TrapError)
                return false;
            throw;
        }
    }

    /// <summary>
    /// Backward-compatible overload (no DataError). Always throws on error.
    /// </summary>
    public static void RunCodeunit(int codeunitId)
    {
        RunCodeunitCore(codeunitId, null);
    }

    private static void RunCodeunitCore(int codeunitId, MockRecordHandle? record = null)
    {
        var handle = new MockCodeunitHandle(codeunitId);
        // Invoke the OnRun scope (member ID 0 or find OnRun explicitly)
        var assembly = CurrentAssembly ?? Assembly.GetExecutingAssembly();
        var codeunitType = handle.FindCodeunitType(assembly);
        if (codeunitType == null)
        {
            // System and test toolkit codeunits are no-ops: fire OnRun event so
            // any compiled subscribers execute, but don't throw.
            // User codeunits (10000-129999) still throw — a missing user codeunit
            // indicates a real gap the developer needs to address.
            if (IsSystemCodeunitId(codeunitId) || codeunitId >= 130000)
            {
                AlCompat.FireEvent(EventSubscriberRegistry.ObjectTypeCodeunit, codeunitId, "OnRun");
                return;
            }
            throw new InvalidOperationException(BuildCodeunitNotFoundMessage(codeunitId, assembly));
        }

        var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(codeunitType);
        var initMethod = codeunitType.GetMethod("InitializeComponent",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        initMethod?.Invoke(instance, null);

        // Find and invoke the OnRun method (parameterless or with record parameter)
        // Search both public and non-public since the rewriter may keep OnRun as protected.
        var bindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var onRunMethod = codeunitType.GetMethod("OnRun",
            bindingFlags, null, Type.EmptyTypes, null);
        if (onRunMethod != null)
        {
            onRunMethod.Invoke(instance, null);
            return;
        }

        // Try finding OnRun with a MockRecordHandle parameter (codeunit with TableNo)
        var onRunWithRecord = codeunitType.GetMethod("OnRun",
            bindingFlags, null, new[] { typeof(MockRecordHandle) }, null);
        if (onRunWithRecord != null)
        {
            onRunWithRecord.Invoke(instance, new object?[] { record });
            return;
        }

        // Fallback: any OnRun overload — pass null for all parameters
        var onRunMethods = codeunitType.GetMethods(bindingFlags)
            .Where(m => m.Name == "OnRun").ToArray();
        if (onRunMethods.Length > 0)
        {
            var parameters = onRunMethods[0].GetParameters();
            var args = new object?[parameters.Length];
            // Fill MockRecordHandle parameters with the provided record
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].ParameterType == typeof(MockRecordHandle))
                    args[i] = record;
            }
            onRunMethods[0].Invoke(instance, args);
            return;
        }
    }

    /// <summary>
    /// Routes Assert codeunit (130) method calls to MockAssert static methods.
    /// Uses argument count and types to determine which Assert method to call,
    /// since member IDs from the transpiler don't map to our mock methods.
    /// </summary>
    private static object? InvokeAssert(int memberId, object[] args)
    {
        // Match by argument count and patterns typical for each Assert method
        switch (args.Length)
        {
            case 1:
            {
                // ExpectedError(text) or ExpectedErrorCode(code) or Fail(text)
                var arg0Str = args[0]?.ToString() ?? "";
                var method1 = FindAssertMethodName(memberId);
                if (method1 != null && method1.Contains("Fail", StringComparison.OrdinalIgnoreCase))
                    MockAssert.Fail(arg0Str);
                else if (method1 != null && method1.Contains("ExpectedErrorCode", StringComparison.OrdinalIgnoreCase))
                    MockAssert.ExpectedErrorCode(arg0Str);
                else
                    MockAssert.ExpectedError(arg0Str);
                return null;
            }

            case 2:
                // IsTrue(bool, text), IsFalse(bool, text), ExpectedErrorCode(code, msg),
                // RecordCount(record, count), ExpectedMessage(expected, actual)
                if (args[0] is MockRecordHandle rec2 && args[1] is int count)
                {
                    MockAssert.RecordCount(rec2, count);
                    return null;
                }
                if (args[0] is bool || args[0] is NavBoolean ||
                    (args[0] is MockVariant mv0 && (mv0.Value is bool || mv0.Value is NavBoolean)))
                {
                    string msg = args[1]?.ToString() ?? "";
                    // Use member ID to distinguish IsTrue from IsFalse
                    var assertMethod = FindAssertMethodName(memberId);
                    if (assertMethod != null && assertMethod.Contains("IsFalse", StringComparison.OrdinalIgnoreCase))
                        MockAssert.IsFalse(args[0], msg);
                    else
                        MockAssert.IsTrue(args[0], msg);
                    return null;
                }
                // Fallback: use method name lookup to distinguish between 2-arg Assert methods
                var method2 = FindAssertMethodName(memberId);
                if (method2 != null && method2.Contains("ExpectedTestFieldError", StringComparison.OrdinalIgnoreCase))
                {
                    MockAssert.ExpectedTestFieldError(args[0]?.ToString() ?? "", args[1]?.ToString() ?? "");
                    return null;
                }
                if (method2 != null && method2.Contains("ExpectedErrorCode", StringComparison.OrdinalIgnoreCase))
                {
                    MockAssert.ExpectedErrorCode(args[0]?.ToString() ?? "", args[1]?.ToString() ?? "");
                    return null;
                }
                // Default: treat as ExpectedMessage(expectedSubstring, actualError)
                MockAssert.ExpectedMessage(args[0]?.ToString() ?? "", args[1]?.ToString() ?? "");
                return null;

            case 3:
                // AreEqual(expected, actual, message) or AreNotEqual(expected, actual, message)
                // We need to distinguish — use method name lookup from the assembly if available,
                // otherwise default to AreEqual (far more common in BC test suites)
                var expected = args[0];
                var actual = args[1];
                var message = args[2]?.ToString() ?? "";

                // Try to find the method name from the generated assembly to distinguish AreEqual vs AreNotEqual
                var methodName = FindAssertMethodName(memberId);
                if (methodName != null && methodName.Contains("AreNotEqual", StringComparison.OrdinalIgnoreCase))
                {
                    MockAssert.AreNotEqual(expected, actual, message);
                }
                else
                {
                    MockAssert.AreEqual(expected, actual, message);
                }
                return null;

            case 4:
                // AreNearlyEqual(expected, actual, delta, message)
                MockAssert.AreNearlyEqual(args[0], args[1], args[2], args[3]?.ToString() ?? "");
                return null;

            default:
                // Unknown Assert method — no-op rather than crash
                return null;
        }
    }

    /// <summary>
    /// Tries to find the Assert method name by looking up the scope class
    /// with the given member ID in the generated Codeunit130 type.
    /// </summary>
    private static string? FindAssertMethodName(int memberId)
    {
        var assembly = CurrentAssembly;
        if (assembly == null) return null;

        var codeunitType = assembly.GetTypes().FirstOrDefault(t => t.Name == "Codeunit130" || t.Name == "Codeunit131");
        if (codeunitType == null) return null;

        var memberIdStr = memberId.ToString();
        var absMemberId = Math.Abs(memberId).ToString();

        foreach (var nested in codeunitType.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (nested.Name.Contains($"_Scope_{memberIdStr}") ||
                nested.Name.Contains($"_Scope__{absMemberId}"))
            {
                var scopeIdx = nested.Name.IndexOf("_Scope_");
                if (scopeIdx >= 0)
                    return nested.Name.Substring(0, scopeIdx);
            }
        }
        return null;
    }

    /// <summary>
    /// Routes Library - Variable Storage (131004) method calls to MockVariableStorage.
    /// Uses method name lookup from the generated Codeunit131004 type.
    /// </summary>
    private object? InvokeVariableStorage(int memberId, object[] args)
    {
        var methodName = FindMethodName(memberId, "Codeunit131004");

        if (methodName != null)
        {
            if (methodName.Contains("Enqueue", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length >= 1)
                {
                    var val = args[0];
                    if (val is MockVariant mv) val = mv.Value;
                    MockVariableStorage.Enqueue(val);
                }
                return null;
            }
            if (methodName.Contains("DequeueText", StringComparison.OrdinalIgnoreCase))
            {
                var val = MockVariableStorage.Dequeue();
                return new NavText(val?.ToString() ?? "");
            }
            if (methodName.Contains("DequeueInteger", StringComparison.OrdinalIgnoreCase))
            {
                var val = MockVariableStorage.Dequeue();
                return ToInt(val);
            }
            if (methodName.Contains("DequeueDecimal", StringComparison.OrdinalIgnoreCase))
            {
                var val = MockVariableStorage.Dequeue();
                return ToDecimal(val);
            }
            if (methodName.Contains("DequeueBoolean", StringComparison.OrdinalIgnoreCase))
            {
                var val = MockVariableStorage.Dequeue();
                return ToBool(val);
            }
            if (methodName.Contains("DequeueDate", StringComparison.OrdinalIgnoreCase))
            {
                var val = MockVariableStorage.Dequeue();
                if (val is NavDate nd) return nd;
                return val ?? NavDate.Default;
            }
            if (methodName.Contains("DequeueVariant", StringComparison.OrdinalIgnoreCase))
            {
                var val = MockVariableStorage.Dequeue();
                return new MockVariant(val);
            }
            if (methodName.Contains("AssertEmpty", StringComparison.OrdinalIgnoreCase))
            {
                MockVariableStorage.AssertEmpty();
                return null;
            }
            if (methodName.Contains("IsEmpty", StringComparison.OrdinalIgnoreCase))
            {
                return MockVariableStorage.IsEmpty();
            }
            if (methodName.Equals("Clear", StringComparison.OrdinalIgnoreCase))
            {
                MockVariableStorage.Clear();
                return null;
            }
        }

        // Fallback: try by arg count
        if (args.Length == 1)
        {
            var val = args[0];
            if (val is MockVariant mv) val = mv.Value;
            MockVariableStorage.Enqueue(val);
            return null;
        }
        if (args.Length == 0)
        {
            // Could be Clear, AssertEmpty, IsEmpty, or a Dequeue — ambiguous without method name
            throw new InvalidOperationException(
                $"Cannot determine which Library - Variable Storage method to call for member {memberId} with 0 args");
        }

        return null;
    }

    /// <summary>
    /// Routes "AL Runner Config" (131100) method calls to MockSession.
    /// Exposes runner-only configuration — CompanyName and CompanyProperty values — to AL code.
    /// </summary>
    private static object? InvokeRunnerConfig(int memberId, object[] args)
    {
        var methodName = FindMethodName(memberId, "Codeunit131100");

        if (methodName != null && methodName.Equals("SetCompanyName", StringComparison.OrdinalIgnoreCase))
        {
            var name = args.Length >= 1 ? (args[0]?.ToString() ?? string.Empty) : string.Empty;
            MockSession.SetCompanyName(name);
            return null;
        }
        if (methodName != null && methodName.Equals("GetCompanyName", StringComparison.OrdinalIgnoreCase))
            return new NavText(MockSession.GetCompanyName());

        if (methodName != null && methodName.Equals("SetCompanyDisplayName", StringComparison.OrdinalIgnoreCase))
        {
            var name = args.Length >= 1 ? (args[0]?.ToString() ?? string.Empty) : string.Empty;
            MockSession.SetCompanyDisplayName(name);
            return null;
        }
        if (methodName != null && methodName.Equals("GetCompanyDisplayName", StringComparison.OrdinalIgnoreCase))
            return new NavText(MockSession.GetCompanyDisplayName());

        if (methodName != null && methodName.Equals("SetCompanyUrlName", StringComparison.OrdinalIgnoreCase))
        {
            var name = args.Length >= 1 ? (args[0]?.ToString() ?? string.Empty) : string.Empty;
            MockSession.SetCompanyUrlName(name);
            return null;
        }
        if (methodName != null && methodName.Equals("GetCompanyUrlName", StringComparison.OrdinalIgnoreCase))
            return new NavText(MockSession.GetCompanyUrlName());

        if (methodName != null && methodName.Equals("SetCompanyId", StringComparison.OrdinalIgnoreCase))
        {
            Guid id = Guid.Empty;
            if (args.Length >= 1)
            {
                if (args[0] is NavGuid ng) id = ng.Value;
                else if (args[0] is Guid g) id = g;
                else Guid.TryParse(args[0]?.ToString(), out id);
            }
            MockSession.SetCompanyId(id);
            return null;
        }
        if (methodName != null && methodName.Equals("GetCompanyId", StringComparison.OrdinalIgnoreCase))
            return new NavGuid(MockSession.GetCompanyId());

        // Fallback: 1 string arg = SetCompanyName, 0 args = GetCompanyName
        if (args.Length >= 1)
        {
            MockSession.SetCompanyName(args[0]?.ToString() ?? string.Empty);
            return null;
        }
        return new NavText(MockSession.GetCompanyName());
    }

    /// <summary>
    /// Tries to find a method name by looking up the scope class
    /// with the given member ID in the specified codeunit type.
    /// </summary>
    private static string? FindMethodName(int memberId, string codeunitTypeName)
    {
        var assembly = CurrentAssembly;
        if (assembly == null) return null;

        var codeunitType = assembly.GetTypes().FirstOrDefault(t => t.Name == codeunitTypeName);
        if (codeunitType == null) return null;

        var memberIdStr = memberId.ToString();
        var absMemberId = Math.Abs(memberId).ToString();

        foreach (var nested in codeunitType.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (nested.Name.Contains($"_Scope_{memberIdStr}") ||
                nested.Name.Contains($"_Scope__{absMemberId}"))
            {
                var scopeIdx = nested.Name.IndexOf("_Scope_");
                if (scopeIdx >= 0)
                    return nested.Name.Substring(0, scopeIdx);
            }
        }
        return null;
    }

    private static int ToInt(object? value)
    {
        if (value is int i) return i;
        if (value is Decimal18 d18) return (int)(decimal)d18;
        if (value is MockVariant mv) return ToInt(mv.Value);
        try { return Convert.ToInt32(value); }
        catch { return 0; }
    }

    private static Decimal18 ToDecimal(object? value)
    {
        if (value is Decimal18 d18) return d18;
        if (value is decimal d) return new Decimal18(d);
        if (value is MockVariant mv) return ToDecimal(mv.Value);
        try { return new Decimal18(Convert.ToDecimal(value)); }
        catch { return new Decimal18(0m); }
    }

    private static bool ToBool(object? value)
    {
        if (value is bool b) return b;
        if (value is MockVariant mv) return ToBool(mv.Value);
        if (value is NavBoolean nb) return (bool)nb;
        try { return Convert.ToBoolean(value); }
        catch { return false; }
    }

    // Cache: assembly → (typeName → Type) to avoid repeated GetTypes() scans.
    // GetTypes() is O(N) per assembly. With 7,699 FindCodeunitType calls and 1,130
    // FindTypeAcrossAssemblies calls per test run, caching saves ~240ms.
    private static readonly Dictionary<Assembly, Dictionary<string, Type>> _typeCache = new();

    internal static Type? LookupTypeByName(Assembly assembly, string name)
    {
        if (!_typeCache.TryGetValue(assembly, out var map))
        {
            map = new Dictionary<string, Type>(StringComparer.Ordinal);
            foreach (var t in assembly.GetTypes())
                map.TryAdd(t.Name, t);
            _typeCache[assembly] = map;
        }
        return map.TryGetValue(name, out var result) ? result : null;
    }

    private Type? FindCodeunitType(Assembly assembly)
    {
        var expectedName = $"Codeunit{_codeunitId}";
        var type = LookupTypeByName(assembly, expectedName);
        if (type != null) return type;

        // Search dependency assemblies
        if (DependencyAssemblies != null)
        {
            foreach (var depAsm in DependencyAssemblies)
            {
                type = LookupTypeByName(depAsm, expectedName);
                if (type != null) return type;
            }
        }
        return null;
    }

    /// <summary>
    /// Returns true when <paramref name="codeunitId"/> falls in the BC system/platform
    /// range (1–9999). System codeunits are part of the BC service tier and cannot be
    /// provided as user stubs; calls to missing system codeunits are treated as no-ops.
    /// </summary>
    private static bool IsSystemCodeunitId(int codeunitId)
        => codeunitId is >= 1 and <= 9999;

    /// <summary>
    /// Build a descriptive error message when a codeunit is not found in the assembly.
    /// Includes the codeunit ID, a hint about common causes, and suggestions for
    /// --stubs or --generate-stubs to resolve the issue.
    /// </summary>
    private static string BuildCodeunitNotFoundMessage(int codeunitId, Assembly assembly)
    {
        var msg = $"Codeunit {codeunitId} not found in assembly.";

        // Classify the codeunit range for targeted hints
        if (codeunitId is >= 130000 and <= 139999)
            msg += $" Codeunit {codeunitId} appears to be from the BC test toolkit.";
        else if (codeunitId is >= 1 and <= 9999)
            msg += $" Codeunit {codeunitId} appears to be a system codeunit.";

        // List available codeunits to help debugging
        var available = assembly.GetTypes()
            .Where(t => t.Name.StartsWith("Codeunit") && t.Name.Length > "Codeunit".Length)
            .Select(t =>
            {
                var idStr = t.Name.Substring("Codeunit".Length);
                return int.TryParse(idStr, out var id) ? id : -1;
            })
            .Where(id => id >= 0)
            .OrderBy(id => id)
            .ToList();

        if (available.Count > 0 && available.Count <= 20)
            msg += $" Available codeunits: {string.Join(", ", available)}.";
        else if (available.Count > 20)
            msg += $" {available.Count} codeunits available in assembly (use --dump-rewritten to inspect).";

        msg += "\nTo fix: generate stubs from your .app packages:\n"
             + "  al-runner --generate-stubs .alpackages ./stubs ./src ./test\n"
             + "Then run with: al-runner --stubs ./stubs ./src ./test";

        return msg;
    }

    /// <summary>
    /// Score how well a reflected method's parameter types match the supplied arguments.
    /// Used by both <see cref="MockCodeunitHandle"/> and <see cref="MockReportHandle"/>
    /// for overload resolution during reflection-based dispatch.
    /// Unwraps <see cref="MockVariant"/> to check the underlying value's type.
    /// </summary>
    internal static int ScoreMethodMatch(MethodInfo method, object[] args)
    {
        int score = 0;
        var parameters = method.GetParameters();
        for (int i = 0; i < parameters.Length && i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == null) continue;

            // Unwrap MockVariant to get the actual underlying value
            if (arg is MockVariant mv && mv.Value != null)
                arg = mv.Value;

            var argType = arg.GetType();
            var paramType = parameters[i].ParameterType;

            if (paramType.IsAssignableFrom(argType))
                score += 10; // exact or inherited match
            else if (paramType == typeof(object))
                score += 5; // object accepts anything
            else
                score += 1; // may be convertible
        }
        return score;
    }

    internal static object? ConvertArg(object? arg, Type targetType) => ConvertArgInternal(arg, targetType);

    private static object? ConvertArgInternal(object? arg, Type targetType)
    {
        if (arg == null) return null;
        if (targetType.IsAssignableFrom(arg.GetType())) return arg;

        // NavScope parameters are used for return-value parent scoping.
        // In standalone mode, pass null — our AlScope doesn't extend NavScope.
        if (targetType == typeof(NavScope) || targetType.IsSubclassOf(typeof(NavScope)))
            return null;

        // MockVariant -> unwrap to underlying value and retry
        if (arg is MockVariant mv)
        {
            return ConvertArgInternal(mv.Value, targetType);
        }

        // MockCodeunitHandle -> MockInterfaceHandle (AL interface injection)
        if (targetType == typeof(MockInterfaceHandle) && arg is MockCodeunitHandle codeunitHandle)
        {
            var ifHandle = new MockInterfaceHandle();
            ifHandle.ALAssign(codeunitHandle);
            return ifHandle;
        }

        // int/decimal -> Decimal18 conversion
        if (targetType.Name == "Decimal18")
        {
            var intCtor = targetType.GetConstructor(new[] { typeof(int) });
            if (intCtor != null && arg is int intVal)
                return intCtor.Invoke(new object[] { intVal });
            var decCtor = targetType.GetConstructor(new[] { typeof(decimal) });
            if (decCtor != null)
            {
                decimal decVal = Convert.ToDecimal(arg);
                return decCtor.Invoke(new object[] { decVal });
            }
        }

        // object -> MockVariant conversion (Variant parameters in AL)
        if (targetType == typeof(MockVariant))
        {
            return new MockVariant(arg);
        }

        // object -> NavVariant conversion (Variant parameters in AL)
        if (targetType.Name == "NavVariant")
        {
            // NavVariant.Create(object) or NavVariant(object) constructor
            var createMethod = targetType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(object) }, null);
            if (createMethod != null)
                return createMethod.Invoke(null, new[] { arg });
            // Try constructor taking object
            var ctor = targetType.GetConstructor(new[] { typeof(object) });
            if (ctor != null)
                return ctor.Invoke(new[] { arg });
            // Fall back: try parameterless + set value
            var defaultCtor = targetType.GetConstructor(Type.EmptyTypes);
            if (defaultCtor != null)
                return defaultCtor.Invoke(null);
        }

        // string -> NavText conversion
        if (targetType.Name == "NavText" && arg is string strVal)
        {
            var ctor = targetType.GetConstructor(new[] { typeof(string) });
            if (ctor != null)
                return ctor.Invoke(new object[] { strVal });
            var ctorWithLen = targetType.GetConstructor(new[] { typeof(int), typeof(string) });
            if (ctorWithLen != null)
                return ctorWithLen.Invoke(new object[] { 250, strVal });
        }

        // NavText -> string conversion (when target is string)
        if (targetType == typeof(string) && arg is NavValue navVal)
        {
            return navVal.ToString();
        }

        // NavValue -> primitive conversions (for comparing NavText/NavOption/NavBoolean with primitives)
        if (arg is NavValue)
        {
            if (targetType == typeof(int))
            {
                var toInt = arg.GetType().GetMethod("ToInt32", Type.EmptyTypes);
                if (toInt != null) return toInt.Invoke(arg, null);
            }
            if (targetType == typeof(bool))
            {
                var toBool = arg.GetType().GetMethod("ToBoolean", Type.EmptyTypes);
                if (toBool != null) return toBool.Invoke(arg, null);
            }
        }

        // Try general conversion
        try { return Convert.ChangeType(arg, targetType); }
        catch { return arg; }
    }
}
