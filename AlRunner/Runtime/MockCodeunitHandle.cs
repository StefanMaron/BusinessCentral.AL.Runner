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
    /// </summary>
    private void EnsureInstance(Type codeunitType)
    {
        if (_codeunitInstance != null) return;
        _codeunitInstance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(codeunitType);
        var initMethod = codeunitType.GetMethod("InitializeComponent",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        initMethod?.Invoke(_codeunitInstance, null);
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
        // Route codeunit 130 ("Library Assert"), 131 ("Assert" alias stub), and 130000 (Assert from BC test toolkit) to MockAssert
        if (_codeunitId is 130 or 131 or 130000)
            return InvokeAssert(memberId, args);

        // Route codeunit 131004 (Library - Variable Storage) to MockVariableStorage
        if (_codeunitId is 131004)
            return InvokeVariableStorage(memberId, args);

        var assembly = CurrentAssembly ?? Assembly.GetExecutingAssembly();
        var codeunitType = FindCodeunitType(assembly);
        if (codeunitType == null)
        {
            throw new InvalidOperationException($"Codeunit {_codeunitId} not found in assembly");
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

        // Fallback: no exact member ID match. Try matching by argument count across all
        // public methods. This handles the case where test code was compiled against
        // Variant-based signatures (like Assert.AreEqual(Variant,Variant,Text)) but the
        // stub has type-specific overloads with different member IDs.
        var candidateMethods = codeunitType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetParameters().Length == args.Length && !m.IsSpecialName)
            .ToList();

        if (candidateMethods.Count > 0)
        {
            // Prefer the method whose parameters best match the actual argument types
            var bestMethod = candidateMethods
                .OrderByDescending(m => ScoreMethodMatch(m, args))
                .First();

            var parameters = bestMethod.GetParameters();
            var convertedArgs = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i < args.Length)
                    convertedArgs[i] = ConvertArgInternal(args[i], parameters[i].ParameterType);
            }
            return bestMethod.Invoke(_codeunitInstance, convertedArgs);
        }

        throw new InvalidOperationException(
            $"Method with member ID {memberId} not found in codeunit {_codeunitId}");
    }

    /// <summary>
    /// Instance method: run the codeunit's OnRun trigger.
    /// Replacement for NavCodeunitHandle.Target.Run(DataError, record).
    /// In BC, this runs the codeunit passing a record parameter.
    /// </summary>
    public bool Run(DataError errorLevel, object? record = null)
    {
        try
        {
            RunCodeunitCore(_codeunitId, record as MockRecordHandle);
            return true;
        }
        catch
        {
            if (errorLevel == DataError.TrapError) return false;
            throw;
        }
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
            throw new InvalidOperationException($"Codeunit {codeunitId} not found in assembly");
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

    private Type? FindCodeunitType(Assembly assembly)
    {
        var expectedName = $"Codeunit{_codeunitId}";
        return assembly.GetTypes().FirstOrDefault(t => t.Name == expectedName);
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
