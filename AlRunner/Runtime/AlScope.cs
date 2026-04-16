using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
namespace AlRunner.Runtime;

/// <summary>
/// Minimal base class replacing NavMethodScope&lt;T&gt; for standalone execution.
/// Provides stub implementations of the debug-hit methods and a Run() entry point.
///
/// Implements <see cref="ITreeObject"/> with stub properties so that scope's
/// <c>this</c> reference can be passed to any BC <c>Nav*</c> type constructor
/// that requires a non-null <c>ITreeObject</c> parent — without a rewriter
/// replacement and without triggering the null-check in NavComplexValue.
/// </summary>
public class AlScope : IDisposable, ITreeObject
{
    // ITreeObject — stub properties, never inspected in standalone mode.
    // NavComplexValue validates parent != null in its constructor; it stores the
    // parent reference but does not call parent.Tree during construction (empirically
    // verified: NullifyFirstThisArgMethods already passes null! as the ITreeObject
    // for blob/stream method calls and those work without NullReferenceException).
    // This pattern is identical to MockInterfaceHandle, which also implements
    // ITreeObject with Tree => null! and is used successfully throughout the suite.
    TreeHandler ITreeObject.Tree => null!;
    TreeObjectType ITreeObject.Type => default;
    bool ITreeObject.SingleThreaded => false;

    protected virtual void OnRun() { }

    public void Run() => OnRun();

    /// <summary>
    /// BC's RunBehavior — wraps the OnRun call with commit/error behavior control.
    /// Generated code calls: scope.RunBehavior(false, CommitBehavior.Ignore, null);
    /// Or for [ErrorBehavior(ErrorBehavior::Collect)]:
    ///   scope.RunBehavior(false, null, ErrorBehavior.Collect);
    /// </summary>
    public void RunBehavior(bool suppressErrors, object? commitBehavior, object? errorBehaviorOrRecord)
    {
        bool enableCollecting = errorBehaviorOrRecord is ErrorBehavior eb && eb == ErrorBehavior.Collect;
        bool wasCollecting = _isCollectingErrors;
        if (enableCollecting)
            _isCollectingErrors = true;
        try
        {
            OnRun();
        }
        finally
        {
            if (enableCollecting)
                _isCollectingErrors = wasCollecting;
        }
    }

    public void Dispose() { }

    // Coverage tracking — the AL compiler emits StmtHit(N) for each statement
    // and CStmtHit(N) for conditional branches.
    private static readonly HashSet<(string Type, int Id)> _hitStatements = new();
    private static readonly HashSet<(string Type, int Id)> _totalStatements = new();

    /// <summary>Last statement hit (scope type name, statement ID) — used for error line mapping.</summary>
    [ThreadStatic] private static (string TypeName, int Id)? _lastStatementHit;

    public static (string TypeName, int Id)? LastStatementHit => _lastStatementHit;

    public static void ResetLastStatement() => _lastStatementHit = null;

    protected void StmtHit(int n)
    {
        _hitStatements.Add((GetType().Name, n));
        _lastStatementHit = (GetType().Name, n);
        IterationTracker.RecordHit(n);
    }

    protected bool CStmtHit(int n)
    {
        _hitStatements.Add((GetType().Name, n));
        _lastStatementHit = (GetType().Name, n);
        IterationTracker.RecordHit(n);
        return true;
    }

    /// <summary>Register a statement ID as existing (for total count).</summary>
    public static void RegisterStatement(string typeName, int id)
    {
        _totalStatements.Add((typeName, id));
    }

    /// <summary>Reset coverage data between test runs (called by Executor).</summary>
    public static void ResetCoverage()
    {
        _hitStatements.Clear();
        _totalStatements.Clear();
    }

    /// <summary>Get coverage results: (typeName, hitCount, totalCount) per scope class.</summary>
    public static List<(string TypeName, int Hit, int Total)> GetCoverageByType()
    {
        var allTypes = new HashSet<string>(
            _totalStatements.Select(s => s.Type)
                .Concat(_hitStatements.Select(s => s.Type)));

        var result = new List<(string, int, int)>();
        foreach (var type in allTypes.OrderBy(t => t))
        {
            var total = _totalStatements.Count(s => s.Type == type);
            var hit = _hitStatements.Count(s => s.Type == type);
            if (total > 0)
                result.Add((type, hit, total));
        }
        return result;
    }

    /// <summary>Get raw coverage sets for report generation.</summary>
    public static (HashSet<(string Type, int Id)> Hit, HashSet<(string Type, int Id)> Total) GetCoverageSets()
    {
        return (_hitStatements, _totalStatements);
    }

    /// <summary>Get overall coverage: (hit, total).</summary>
    public static (int Hit, int Total) GetOverallCoverage()
    {
        return (_hitStatements.Count, _totalStatements.Count);
    }

    /// <summary>
    /// AL's asserterror keyword - catches expected errors.
    /// Sets the last error text for Assert.ExpectedError() to check.
    /// </summary>
    protected void AssertError(Action action)
    {
        try
        {
            action();
            LastErrorText = "";
        }
        catch (Exception ex)
        {
            // Unwrap TargetInvocationException from reflection calls
            var inner = ex;
            while (inner is System.Reflection.TargetInvocationException tie && tie.InnerException != null)
                inner = tie.InnerException;
            LastErrorText = inner.Message;
        }
    }

    /// <summary>
    /// Stores the last error message from asserterror blocks.
    /// </summary>
    public static string LastErrorText { get; set; } = "";

    /// <summary>
    /// Configurable user ID returned by UserId() — defaults to "TESTUSER".
    /// Set via --user-id CLI flag or PipelineOptions.UserId before running.
    /// </summary>
    public static string UserId { get; set; } = "TESTUSER";

    // NavMethodScope static fields/methods — the BC compiler can emit static
    // references to these on scope classes that inherit from AlScope.
    public static int ExitStatementNumber { get; set; }
    public static int MaxStackDepth { get; set; } = 1000;
    public static string LastErrorCallStack { get; set; } = "";

    // ── Collectible errors ──────────────────────────────────────────────
    // Thread-static to avoid cross-test contamination in parallel scenarios.

    [ThreadStatic] private static bool _isCollectingErrors;
    [ThreadStatic] private static List<NavALErrorInfo>? _collectedErrors;

    internal static List<NavALErrorInfo> CollectedErrors => _collectedErrors ??= new();

    /// <summary>Returns true when at least one collectible error has been recorded.</summary>
    public static bool HasCollectedErrors => CollectedErrors.Count > 0;

    /// <summary>Returns true when execution is inside an [ErrorBehavior(Collect)] scope.</summary>
    public static bool IsCollectingErrors => _isCollectingErrors;

    /// <summary>Returns the collected errors, optionally clearing the list.</summary>
    public static NavList<NavALErrorInfo> GetCollectedErrors(bool clearErrors)
    {
        var result = NavList<NavALErrorInfo>.Default;
        foreach (var err in CollectedErrors)
            result.ALAdd(err);
        if (clearErrors)
            CollectedErrors.Clear();
        return result;
    }

    /// <summary>Clears all collected errors.</summary>
    public static void ClearCollectedErrors() => CollectedErrors.Clear();

    /// <summary>Reset collected errors state between tests.</summary>
    public static void ResetCollectedErrors()
    {
        _isCollectingErrors = false;
        _collectedErrors = null;
    }

    public static AlScope? FindTryMethodScope(AlScope scope)
    {
        // In the real runtime this walks the scope chain looking for a
        // try-function scope. In standalone mode, return null (no try scope).
        return null;
    }

    public static string MethodName(int memberId)
    {
        // In the real runtime this maps member IDs to method names for
        // diagnostics. Return a placeholder in standalone mode.
        return $"Method_{memberId}";
    }

    /// <summary>
    /// AL's [TryFunction] attribute. BC emits
    /// <c>TryInvoke(() =&gt; base.Parent.TryMethod())</c> at call sites.
    /// Executes the delegate; returns true if it completes without throwing,
    /// false if any exception escapes. The method's own return value is
    /// discarded — BC always reports TryFunction status via true/false.
    /// </summary>
    protected static bool TryInvoke(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Overload for TryFunction wrappers that return a value (AL's TryFunction
    /// always returns Boolean to the caller, but the underlying lambda may
    /// return the wrapped method's own value — BC's generated C# uses
    /// <c>Func&lt;T&gt;</c> when the method had a non-void signature).
    /// </summary>
    protected static bool TryInvoke<T>(Func<T> func)
    {
        try
        {
            func();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Instance replacement for NavDialog (the BC dialog/progress window object).
/// No-op CurrPage stub for page extensions.
/// Page extension code calls CurrPage.Update(), CurrPage.Editable, etc.
/// In standalone mode, all operations are no-ops.
/// </summary>
public class MockCurrPage
{
    public bool Editable { get; set; }
    public void Update(bool saveRecord = true) { }
    public void Close() { }
    public void Activate() { }
    public void SaveRecord() { }
}

/// <summary>
/// The transpiled code creates NavDialog instances for progress bars (ALOpen, ALUpdate, ALClose).
/// In standalone mode, we no-op these operations.
/// </summary>
public class MockDialog
{
    public MockDialog() { }

    public void ALOpen(Guid id, NavText text) { }
    public void ALOpen(Guid id, NavText text, NavText text2) { }
    // BC compiler emits string literals directly in some ALOpen calls
    public void ALOpen(Guid id, string text) { }
    public void ALOpen(Guid id, string text, string text2) { }
    public void ALOpen(Guid id, string text, NavText text2) { }
    public void ALOpen(Guid id, NavText text, string text2) { }
    public void ALUpdate(int fieldNo, NavValue value) { }
    public void ALUpdate(int fieldNo, string value) { }
    public void ALUpdate(int fieldNo, int value) { }
    public void ALUpdate(int fieldNo, NavText value) { }
    public void ALClose() { }
    public void ALAssign(MockDialog other) { }

    // For ByRef<NavDialog> patterns that access .Value
    public MockDialog Value => this;

    /// <summary>
    /// AL's CONFIRM dialog. If a ConfirmHandler is registered, dispatches to it.
    /// Otherwise returns true (user confirmed) in standalone mode.
    /// </summary>
    public static bool ALConfirm(string question, bool defaultButton = false, params object?[] args)
    {
        var (handled, reply) = HandlerRegistry.InvokeConfirmHandler(question);
        return handled ? reply : true;
    }

    /// <summary>
    /// AL's CONFIRM dialog with NavText parameters.
    /// </summary>
    public static bool ALConfirm(NavText question, bool defaultButton = false)
    {
        var (handled, reply) = HandlerRegistry.InvokeConfirmHandler(question.ToString());
        return handled ? reply : true;
    }

    /// <summary>
    /// AL's CONFIRM dialog with Guid (for dialog ID) and NavText.
    /// </summary>
    public static bool ALConfirm(Guid dialogId, NavText question, bool defaultButton)
    {
        var (handled, reply) = HandlerRegistry.InvokeConfirmHandler(question.ToString());
        return handled ? reply : true;
    }
}

/// <summary>
/// Replacement for NavDialog static methods.
/// Translates AL Message/Error calls to console output / exceptions.
/// </summary>
public static class AlDialog
{
    public static void Message(string format, params object?[] args)
    {
        var netFormat = ConvertAlFormat(format);
        var stringArgs = args.Select(a => AlCompat.Format(a)).ToArray();
        string formatted;
        if (stringArgs.Length > 0)
            formatted = string.Format(netFormat, stringArgs);
        else
            formatted = format;

        // If a MessageHandler is registered, dispatch to it instead of printing
        if (HandlerRegistry.InvokeMessageHandler(formatted))
            return;

        Console.WriteLine(formatted);
        MessageCapture.Capture(formatted);
    }

    public static void Error(string format, params object?[] args)
    {
        var netFormat = ConvertAlFormat(format);
        var stringArgs = args.Select(a => AlCompat.Format(a)).ToArray();
        if (stringArgs.Length > 0)
            throw new Exception(string.Format(netFormat, stringArgs));
        else
            throw new Exception(format);
    }

    /// <summary>
    /// Overload for AL's Error(ErrorInfo) pattern where NavALErrorInfo is passed directly.
    /// When the error is collectible and collecting mode is active, the error is
    /// added to the collected errors list instead of throwing.
    /// </summary>
    public static void Error(Microsoft.Dynamics.Nav.Runtime.NavALErrorInfo errorInfo)
    {
        var message = errorInfo?.ALMessage ?? "Error";

        if (errorInfo != null && errorInfo.ALCollectible && AlScope.IsCollectingErrors)
        {
            AlScope.CollectedErrors.Add(errorInfo);
            return;
        }

        throw new Exception(message);
    }

    /// <summary>
    /// Converts AL format placeholders (%1, %2, ...) to .NET format ({0}, {1}, ...).
    /// </summary>
    private static string ConvertAlFormat(string alFormat)
    {
        var result = alFormat;
        for (int i = 9; i >= 1; i--)
            result = result.Replace($"%{i}", $"{{{i - 1}}}");
        return result;
    }
}

/// <summary>
/// Lightweight replacement for ALCompiler static methods that depend on NavSession.
/// These methods are used in generated C# code for type conversions.
/// </summary>
public static class AlCompat
{
    /// <summary>
    /// Return the declared ordinals for an AL enum by inspecting the
    /// generated <c>Enum{id}</c> class at runtime. Used by the rewriter
    /// rule that intercepts <c>Enum::X.Ordinals()</c>, which BC lowers to
    /// <c>NCLEnumMetadata.Create(id).GetOrdinals()</c> — a native runtime
    /// call that throws "not supported" under standalone mode.
    /// </summary>
    /// <summary>
    /// Registry of AL enum ordinals and names, populated at transpile time
    /// (see <see cref="EnumRegistry.ParseAndRegister"/>). BC doesn't emit
    /// a C# class for pure enums — values are inlined at call sites — so
    /// reflection isn't an option; we parse the AL source headers instead.
    /// </summary>
    public static NavList<int> GetEnumOrdinals(int enumObjectId)
    {
        var list = NavList<int>.Default;
        foreach (var (ord, _) in EnumRegistry.GetMembers(enumObjectId))
            list.ALAdd(ord);
        return list;
    }

    public static NavList<NavText> GetEnumNames(int enumObjectId)
    {
        var list = NavList<NavText>.Default;
        foreach (var (_, name) in EnumRegistry.GetMembers(enumObjectId))
            list.ALAdd(new NavText(name));
        return list;
    }

    /// <summary>
    /// ConditionalWeakTable mapping a NavOption instance to the AL enum
    /// object id it was constructed with. Used by
    /// <see cref="GetOrdinalsForOption"/> / <see cref="GetNamesForOption"/>
    /// so <c>someEnumVar.Ordinals() / .Names()</c> (which BC lowers to
    /// <c>navOption.ALOrdinals / .ALNames</c> and hits NCLOptionMetadata
    /// native code) can resolve the enum without the now-lost metadata.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<NavOption, object> _optionEnumId = new();

    /// <summary>
    /// Create a NavOption and remember its source enum object ID so later
    /// calls to <see cref="GetOrdinalsForOption"/> / <see cref="GetNamesForOption"/>
    /// can resolve back to the AL enum. Emitted by the rewriter as the
    /// replacement for <c>NavOption.Create(NCLEnumMetadata.Create(N), V)</c>.
    /// </summary>
    public static NavOption CreateTaggedOption(int enumObjectId, int ordinal)
    {
        var opt = AlRunner.Runtime.MockRecordHandle.CreateOptionValue(ordinal);
        _optionEnumId.AddOrUpdate(opt, enumObjectId);
        return opt;
    }

    /// <summary>
    /// Dispatch AL [EventSubscriber] procedures registered against a
    /// [IntegrationEvent] / [BusinessEvent] raised by the publisher at
    /// (codeunitId, eventName). Called from generated event-method
    /// bodies by the rewriter in place of the now-stripped
    /// <c>βscope.RunEvent()</c> call.
    ///
    /// The subscriber registry is populated lazily by scanning the
    /// current assembly for methods carrying
    /// <c>NavEventSubscriberAttribute</c> the first time FireEvent is
    /// called — later runs on the same assembly reuse the cache.
    /// </summary>
    public static void FireEvent(int publisherId, string eventName, params object?[] eventArgs)
    {
        FireEvent(EventSubscriberRegistry.ObjectTypeCodeunit, publisherId, eventName, eventArgs);
    }

    /// <summary>
    /// Fire an event, dispatching to all registered subscribers.
    /// Manual subscribers are only dispatched if a matching instance is
    /// currently bound via <see cref="EventSubscriberRegistry.Bind"/>.
    /// </summary>
    public static void FireEvent(int objectType, int publisherId, string eventName, params object?[] eventArgs)
    {
        var asm = MockCodeunitHandle.CurrentAssembly;
        if (asm == null) return;
        var subs = EventSubscriberRegistry.GetSubscribers(asm, objectType, publisherId, eventName);
        foreach (var sub in subs)
        {
            var ownerType = sub.OwnerType;
            var method = sub.Method;

            // Manual subscribers: only fire if a bound instance exists
            if (EventSubscriberRegistry.IsManualSubscriber(ownerType))
            {
                foreach (var boundInst in EventSubscriberRegistry.GetBoundInstances(ownerType))
                    InvokeSubscriber(boundInst, method, eventArgs);
                continue;
            }

            // Automatic subscribers: create a fresh instance
            object? instance;
            try
            {
                instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(ownerType);
                var initMethod = ownerType.GetMethod("InitializeComponent",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                initMethod?.Invoke(instance, null);
            }
            catch { continue; }

            InvokeSubscriber(instance, method, eventArgs);
        }
    }

    private static void InvokeSubscriber(object? instance, System.Reflection.MethodInfo method, object?[] eventArgs)
    {
        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];
        for (int i = 0; i < args.Length && i < eventArgs.Length; i++)
            args[i] = eventArgs[i];
        try { method.Invoke(instance, args); }
        catch (System.Reflection.TargetInvocationException tie)
        {
            if (tie.InnerException != null) throw tie.InnerException;
            throw;
        }
    }

    /// <summary>
    /// Create a NavOption that inherits the enum-id tag from an existing
    /// NavOption. Emitted by the rewriter for
    /// <c>NavOption.Create(existing.NavOptionMetadata, V)</c>
    /// reassignments so the new instance keeps its enum-id lineage.
    /// </summary>
    public static NavOption CloneTaggedOption(NavOption existing, int ordinal)
    {
        var opt = AlRunner.Runtime.MockRecordHandle.CreateOptionValue(ordinal);
        if (_optionEnumId.TryGetValue(existing, out var id))
            _optionEnumId.AddOrUpdate(opt, id);
        return opt;
    }

    /// <summary>
    /// Called by the rewriter for <c>navOption.ALOrdinals</c> property
    /// access. Looks up the tagged enum id and returns its declared
    /// ordinals via <see cref="GetEnumOrdinals"/>; falls back to the
    /// first registered enum that matches the option's own ordinal so
    /// untagged NavOptions still have a chance at the right answer.
    /// </summary>
    public static NavList<int> GetOrdinalsForOption(NavOption opt)
    {
        if (_optionEnumId.TryGetValue(opt, out var idObj) && idObj is int id)
            return GetEnumOrdinals(id);
        return NavList<int>.Default;
    }

    /// <summary>Mirror of <see cref="GetOrdinalsForOption"/> for the Names() path.</summary>
    public static NavList<NavText> GetNamesForOption(NavOption opt)
    {
        if (_optionEnumId.TryGetValue(opt, out var idObj) && idObj is int id)
            return GetEnumNames(id);
        return NavList<NavText>.Default;
    }

    /// <summary>
    /// Replacement for ALCompiler.ToNavValue - wraps a value as NavValue.
    /// NavValue is abstract; we create the appropriate concrete subtype.
    /// The original goes through NavValueFormatter/NavSession; we construct directly.
    /// </summary>
    public static NavValue ToNavValue(object? value)
    {
        if (value == null) return new NavText("");
        if (value is MockVariant mv) return ToNavValue(mv.Value);
        if (value is NavValue nv) return nv;
        if (value is string s) return new NavText(s);
        if (value is int i) return NavInteger.Create(i);
        if (value is decimal d) return NavDecimal.Create(new Microsoft.Dynamics.Nav.Runtime.Decimal18(d));
        if (value is Microsoft.Dynamics.Nav.Runtime.Decimal18 d18) return NavDecimal.Create(d18);
        if (value is bool b) return NavBoolean.Create(b);
        if (value is Guid g) return new NavGuid(g);
        if (value is long l) return NavBigInteger.Create(l);
        // Fall back to string representation
        return new NavText(value.ToString() ?? "");
    }

    /// <summary>
    /// Replacement for ALCompiler.ObjectToDecimal.
    /// </summary>
    public static decimal ObjectToDecimal(object? value)
    {
        if (value == null) return 0m;
        var extracted = ExtractDecimal(value);
        if (extracted.HasValue) return extracted.Value;
        return Convert.ToDecimal(value);
    }

    /// <summary>
    /// Replacement for ALCompiler.ObjectToBoolean.
    /// </summary>
    public static bool ObjectToBoolean(object? value)
    {
        if (value == null) return false;
        return Convert.ToBoolean(value);
    }

    /// <summary>
    /// Replacement for ALCompiler.ToVariant / NavValueToVariant.
    /// Wraps a value as a MockVariant (Variant in AL is NavVariant → MockVariant in rewritten code).
    /// Returns MockVariant so it can be passed to methods expecting MockVariant parameters.
    /// </summary>
    public static MockVariant ToVariant(object? value)
    {
        if (value is MockVariant mv) return mv;
        return new MockVariant(value ?? "");
    }

    /// <summary>
    /// Replacement for ALCompiler.ObjectToNavArray.
    /// Converts a runtime object into the rewritten MockArray shape.
    /// </summary>
    public static MockArray<T> ObjectToMockArray<T>(object? value)
    {
        if (value is MockArray<T> mockArray)
            return mockArray;

        if (value is IEnumerable<T> enumerable)
        {
            var items = enumerable.ToArray();
            var result = new MockArray<T>(default!, items.Length);
            for (int i = 0; i < items.Length; i++)
                result[i] = items[i];
            return result;
        }

        if (value != null && value.GetType().IsGenericType &&
            value.GetType().Name.StartsWith("NavArray", StringComparison.Ordinal))
        {
            try
            {
                var arrayLen = value.GetType().GetMethod("ArrayLen", Type.EmptyTypes);
                var indexer = value.GetType().GetProperty("Item", new[] { typeof(int) });
                var length = arrayLen != null ? Convert.ToInt32(arrayLen.Invoke(value, null)) : 0;
                var result = new MockArray<T>(default!, length);
                if (indexer != null)
                {
                    for (int i = 0; i < length; i++)
                    {
                        var item = indexer.GetValue(value, new object[] { i });
                        if (item is T typedItem)
                            result[i] = typedItem;
                    }
                }
                return result;
            }
            catch (Exception)
            {
                // NavArray reflection failed (type mismatch, missing method, etc.)
                // — fall through to empty default array.
            }
        }

        return new MockArray<T>(default!, 8);
    }

    /// <summary>
    /// Replacement for ALCompiler.NavIndirectValueToBoolean.
    /// Extracts a boolean from a variant/indirect value holder.
    /// </summary>
    public static bool NavIndirectValueToBoolean(object? value)
    {
        if (value is MockVariant mv) return NavIndirectValueToBoolean(mv.Value);
        if (value is bool b) return b;
        if (value is NavBoolean nb) return (bool)nb;
        return Convert.ToBoolean(value);
    }

    /// <summary>
    /// Replacement for ALCompiler.NavIndirectValueToInt32.
    /// Extracts an int from a variant/indirect value holder.
    /// </summary>
    public static int NavIndirectValueToInt32(object? value)
    {
        if (value is MockVariant mv) return NavIndirectValueToInt32(mv.Value);
        if (value is int i) return i;
        if (value is NavInteger ni) return (int)ni;
        return Convert.ToInt32(value);
    }

    /// <summary>
    /// Replacement for ALCompiler.NavIndirectValueToNavValue.
    /// Extracts the NavValue from a variant/indirect value holder.
    /// </summary>
    public static T NavIndirectValueToNavValue<T>(object? value) where T : NavValue
    {
        if (value is T directValue) return directValue;
        if (value is MockVariant mv && mv.Value is T mvValue) return mvValue;
        if (value is NavValue nv && nv is T typedValue) return typedValue;
        // Try conversion from string
        if (typeof(T) == typeof(NavText))
            return (T)(NavValue)new NavText(value?.ToString() ?? "");
        throw new InvalidCastException($"Cannot convert {value?.GetType().Name ?? "null"} to {typeof(T).Name}");
    }

    /// <summary>
    /// Replacement for NavFormatEvaluateHelper.Format.
    /// AL Format() trims trailing zeros from decimals and uses invariant formatting.
    /// </summary>
    public static string Format(object? value)
    {
        if (value == null) return "";
        // Unwrap MockVariant to get the underlying value
        if (value is MockVariant mv) return Format(mv.Value);
        // Handle native .NET numeric types
        if (value is decimal d) return FormatDecimal(d);
        if (value is double dbl) return FormatDecimal((decimal)dbl);
        if (value is float f) return FormatDecimal((decimal)f);
        if (value is int or long or short or byte) return value.ToString()!;
        // Handle Decimal18 and other BC numeric types — convert to decimal
        var typeName = value.GetType().Name;
        if (typeName == "Decimal18")
        {
            try
            {
                var d18 = Convert.ToDecimal(value);
                return FormatDecimal(d18);
            }
            catch { }
        }
        // Handle NavTime — ToString() triggers NavTimeFormatter which requires NavSession
        if (typeName == "NavTime")
        {
            return FormatNavTime(value);
        }
        // Handle NavDate — ToString() triggers NavDateFormatter which may require NavSession
        if (typeName == "NavDate")
        {
            try
            {
                var valProp = value.GetType().GetProperty("Value");
                if (valProp != null)
                {
                    var inner = valProp.GetValue(value);
                    if (inner is DateTime dt)
                        return dt.ToString("yyyy-MM-dd");
                }
            }
            catch { }
        }
        // Handle NavOption — ToString() triggers NavSession via NavOptionFormatter
        if (typeName == "NavOption")
        {
            try
            {
                // NavOption has a Value property (int) we can use
                var valProp = value.GetType().GetProperty("Value");
                if (valProp != null)
                    return valProp.GetValue(value)?.ToString() ?? "";
            }
            catch { }
        }
        // Handle NavValue subtypes — use ToText() where available, avoid ToString() which may need NavSession
        if (value is Microsoft.Dynamics.Nav.Runtime.NavValue nv)
        {
            try
            {
                if (value is Microsoft.Dynamics.Nav.Runtime.NavText nt) return (string)nt;
                if (value is Microsoft.Dynamics.Nav.Runtime.NavBoolean nb) return ((bool)nb).ToString();
                if (value is Microsoft.Dynamics.Nav.Runtime.NavInteger ni) return ((int)ni).ToString();
                if (value is Microsoft.Dynamics.Nav.Runtime.NavBigInteger nbi) return ((long)nbi).ToString();
                if (value is Microsoft.Dynamics.Nav.Runtime.NavGuid ng) return ((Guid)ng).ToString();
                // NavDateTime: cast to DateTime to avoid NavDateTimeFormatter needing NavSession
                if (typeName == "NavDateTime")
                {
                    try { return ((DateTime)(Microsoft.Dynamics.Nav.Runtime.NavDateTime)value).ToString("o", System.Globalization.CultureInfo.InvariantCulture); }
                    catch { return ""; }
                }
                // NavTime/NavDate/NavOption inside NavValue: use reflection to extract raw value
                // These types' ToString() calls NavSession-dependent formatters.
                if (typeName == "NavTime")
                    return FormatNavTime(value);
                if (typeName == "NavDate")
                {
                    var dateProp = value.GetType().GetProperty("Value");
                    if (dateProp != null)
                    {
                        var innerDate = dateProp.GetValue(value);
                        if (innerDate is DateTime dt2)
                            return dt2.ToString("yyyy-MM-dd");
                    }
                    return "";
                }
                if (typeName == "NavOption")
                {
                    var optProp = value.GetType().GetProperty("Value");
                    if (optProp != null)
                        return optProp.GetValue(value)?.ToString() ?? "";
                    return "";
                }
                // For NavDecimal, extract the underlying Decimal18
                var decProp = value.GetType().GetProperty("Value");
                if (decProp != null)
                {
                    var inner = decProp.GetValue(value);
                    if (inner != null) return FormatDecimal(Convert.ToDecimal(inner));
                }
            }
            catch { }
            // For NavValue subtypes that weren't handled above, try ToString()
            // but catch any NavSession-related crashes.
            try { return nv.ToString() ?? ""; }
            catch { return ""; }
        }
        return value.ToString() ?? "";
    }

    /// <summary>
    /// Format with AL format number and length.
    /// Used when AL code calls Format(value, formatNumber) or Format(value, formatNumber, formatLength)
    /// </summary>
    public static string Format(object? value, int formatNumber, int formatLength = 0)
    {
        // For now, ignore the format number/length and use default formatting
        return Format(value);
    }

    /// <summary>
    /// Replacement for ALSystemString.ALStrSubstNo.
    /// AL StrSubstNo replaces %1, %2, ... %N placeholders with formatted argument values.
    /// The real BC implementation routes through NavValueFormatter.Format(NavSession, ...) which
    /// requires NavSession and crashes with NullReferenceException in the runner context.
    /// We use AlCompat.Format() which already handles all BC value types without NavSession.
    /// </summary>
    public static string StrSubstNo(string fmt, params Microsoft.Dynamics.Nav.Runtime.NavValue[] args)
    {
        if (fmt == null) return "";
        var result = fmt;
        for (int i = 0; i < args.Length; i++)
            result = result.Replace("%" + (i + 1), Format(args[i]));
        return result;
    }

    /// <summary>
    /// Format with AL format string (e.g. '&lt;Year4&gt;-&lt;Month,2&gt;-&lt;Day,2&gt;').
    /// The BC transpiler emits NavFormatEvaluateHelper.Format(session, value, length, formatString)
    /// which the rewriter strips the session arg from, producing AlCompat.Format(value, length, formatString).
    /// </summary>
    public static string Format(object? value, int length, string formatString)
    {
        if (!string.IsNullOrEmpty(formatString) && formatString.Contains('<'))
        {
            // Unwrap NavDate / DateTime for date format strings
            DateTime? dt = ExtractDateTime(value);
            if (dt.HasValue)
            {
                var dtResult = ApplyAlFormatString(dt.Value, formatString);
                if (dtResult != null)
                    return dtResult;
            }

            // Handle NavTime with time picture strings (e.g. '<Hours24,2>:<Minutes,2>')
            var typeName = value?.GetType().Name;
            if (typeName == "NavTime")
            {
                var timeResult = ApplyTimeFormatString(value!, formatString);
                if (timeResult != null)
                    return timeResult;
            }

            // Handle decimal / Decimal18 with picture strings (<Precision,min:max> or <Standard Format,N>)
            decimal? dec = ExtractDecimal(value);
            if (dec.HasValue)
            {
                var decResult = ApplyDecimalFormatString(dec.Value, formatString);
                if (decResult != null)
                    return decResult;
            }
        }

        // Fallback: ignore the format string and use default formatting
        return Format(value);
    }

    /// <summary>
    /// Extract a DateTime from various BC/runtime value types.
    /// </summary>
    private static DateTime? ExtractDateTime(object? value)
    {
        if (value is DateTime dt) return dt;
        if (value is MockVariant mv) return ExtractDateTime(mv.Value);
        // NavDate wraps a DateTime — try to extract it
        var typeName = value?.GetType().Name;
        if (typeName == "NavDate" || typeName == "NavDateTime")
        {
            try
            {
                // NavDate has an implicit conversion to DateTime
                return (DateTime)Convert.ChangeType(value!, typeof(DateTime));
            }
            catch
            {
                // Try via Value property
                try
                {
                    var valProp = value!.GetType().GetProperty("Value");
                    if (valProp != null)
                    {
                        var inner = valProp.GetValue(value);
                        if (inner is DateTime innerDt) return innerDt;
                    }
                }
                catch { }
            }
        }
        return null;
    }

    /// <summary>
    /// Apply an AL format string to a DateTime.
    /// Supports tokens: Year4, Year2, Month, Month2, Day, Day2, Hours24, Minutes, Seconds.
    /// The optional ,N suffix on tokens indicates zero-padded width.
    /// Returns null if the format string is not recognized as a date format.
    /// </summary>
    private static string? ApplyAlFormatString(DateTime dt, string formatString)
    {
        // Check if this looks like an AL format string with angle brackets
        if (!formatString.Contains('<'))
            return null;

        var result = new System.Text.StringBuilder();
        int i = 0;
        while (i < formatString.Length)
        {
            if (formatString[i] == '<')
            {
                int end = formatString.IndexOf('>', i);
                if (end < 0)
                {
                    result.Append(formatString[i]);
                    i++;
                    continue;
                }

                string token = formatString.Substring(i + 1, end - i - 1);
                result.Append(ResolveAlDateToken(dt, token));
                i = end + 1;
            }
            else
            {
                result.Append(formatString[i]);
                i++;
            }
        }
        return result.ToString();
    }

    /// <summary>
    /// Resolve a single AL date format token like "Year4", "Month,2", "Day,2".
    /// </summary>
    private static string ResolveAlDateToken(DateTime dt, string token)
    {
        // Parse token name and optional width: "Month,2" → name="Month", width=2
        string name;
        int width = 0;
        int commaPos = token.IndexOf(',');
        if (commaPos >= 0)
        {
            name = token.Substring(0, commaPos).Trim();
            int.TryParse(token.Substring(commaPos + 1).Trim(), out width);
        }
        else
        {
            name = token.Trim();
        }

        string raw = name.ToLowerInvariant() switch
        {
            "year4" => dt.Year.ToString("D4"),
            "year2" => (dt.Year % 100).ToString("D2"),
            "year" => width >= 4 ? dt.Year.ToString("D4") : (dt.Year % 100).ToString("D2"),
            "month" => width > 0 ? dt.Month.ToString($"D{width}") : dt.Month.ToString(),
            "day" => width > 0 ? dt.Day.ToString($"D{width}") : dt.Day.ToString(),
            "hours24" => width > 0 ? dt.Hour.ToString($"D{width}") : dt.Hour.ToString(),
            "hours12" => (dt.Hour % 12 == 0 ? 12 : dt.Hour % 12).ToString(width > 0 ? $"D{width}" : ""),
            "minutes" => width > 0 ? dt.Minute.ToString($"D{width}") : dt.Minute.ToString(),
            "seconds" => width > 0 ? dt.Second.ToString($"D{width}") : dt.Second.ToString(),
            _ => $"<{token}>", // Unknown token — preserve as-is
        };
        return raw;
    }

    /// <summary>
    /// Extract a decimal value from a BC value type (Decimal18, NavDecimal, or plain decimal).
    /// Returns null if the value is not a decimal type.
    /// </summary>
    private static decimal? ExtractDecimal(object? value)
    {
        if (value == null) return null;
        if (value is MockVariant mv) return ExtractDecimal(mv.Value);
        if (value is decimal d) return d;
        if (value is double dbl) return (decimal)dbl;
        if (value is float f) return (decimal)f;
        var typeName = value.GetType().Name;
        if (typeName == "Decimal18" || typeName == "NavDecimal")
        {
            // Try direct conversion first
            try { return Convert.ToDecimal(value); } catch { }
            // For NavDecimal: Value property returns Decimal18; for Decimal18: implicit to decimal
            try
            {
                var valProp = value.GetType().GetProperty("Value");
                if (valProp != null)
                {
                    var inner = valProp.GetValue(value);
                    if (inner == null) return null;
                    // inner may be Decimal18 — try converting it too
                    try { return Convert.ToDecimal(inner); } catch { }
                    // Decimal18 may have its own Value property (double or long)
                    var innerProp = inner.GetType().GetProperty("Value");
                    if (innerProp != null)
                    {
                        var innerVal = innerProp.GetValue(inner);
                        if (innerVal != null) return Convert.ToDecimal(innerVal);
                    }
                }
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Apply an AL decimal picture format string to a decimal value.
    /// Supports:
    ///   &lt;Precision,min:max&gt;  — round to max decimal places, show at least min decimal places
    ///   &lt;Standard Format,N&gt; — N=0 default formatting, N=1 no decimals
    /// Returns null if the format string is not a recognised decimal picture string.
    /// </summary>
    private static string? ApplyDecimalFormatString(decimal value, string formatString)
    {
        var fs = formatString.Trim();
        if (!fs.Contains('<') || !fs.Contains('>'))
            return null;

        // Extract each <...> token. For decimals, multi-token strings like
        // '<Precision,2:2><Standard Format,0>' are valid — Precision takes precedence,
        // otherwise Standard Format applies.
        string? precisionToken = null;
        string? standardFormatToken = null;
        int i = 0;
        while (i < fs.Length)
        {
            int start = fs.IndexOf('<', i);
            if (start < 0) break;
            int end = fs.IndexOf('>', start);
            if (end < 0) break;
            var token = fs.Substring(start + 1, end - start - 1).Trim();
            if (precisionToken == null && token.StartsWith("Precision,", StringComparison.OrdinalIgnoreCase))
                precisionToken = token;
            else if (standardFormatToken == null && token.StartsWith("Standard Format,", StringComparison.OrdinalIgnoreCase))
                standardFormatToken = token;
            i = end + 1;
        }

        if (precisionToken != null)
        {
            var rest = precisionToken.Substring("Precision,".Length);
            var colonPos = rest.IndexOf(':');
            if (colonPos >= 0 &&
                int.TryParse(rest.Substring(0, colonPos).Trim(), out int minDec) &&
                int.TryParse(rest.Substring(colonPos + 1).Trim(), out int maxDec))
            {
                var rounded = Math.Round(value, maxDec, MidpointRounding.AwayFromZero);
                if (maxDec <= 0)
                    return rounded.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
                var fullFmt = "0." + new string('0', maxDec);
                var full = rounded.ToString(fullFmt, System.Globalization.CultureInfo.InvariantCulture);
                if (full.Contains('.'))
                {
                    int dotPos = full.IndexOf('.');
                    int endPos = full.Length;
                    int minEnd = dotPos + 1 + minDec;
                    while (endPos > minEnd && full[endPos - 1] == '0')
                        endPos--;
                    if (endPos == dotPos + 1 && minDec == 0)
                        endPos = dotPos;
                    full = full.Substring(0, endPos);
                }
                return full;
            }
        }

        if (standardFormatToken != null)
        {
            var rest = standardFormatToken.Substring("Standard Format,".Length).Trim();
            if (int.TryParse(rest, out int formatNo))
            {
                return formatNo switch
                {
                    1 => Math.Round(value, 0, MidpointRounding.AwayFromZero)
                             .ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                    _ => FormatDecimal(value),
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Apply an AL time picture format string to a NavTime value.
    /// Parses tokens like &lt;Hours24,N&gt;, &lt;Minutes,N&gt;, &lt;Seconds,N&gt; interleaved with literals.
    /// Returns null if the format string contains no time tokens.
    /// </summary>
    private static string? ApplyTimeFormatString(object navTimeValue, string formatString)
    {
        // Extract the underlying DateTime from NavTime
        DateTime? dt = null;
        try
        {
            var valProp = navTimeValue.GetType().GetProperty("Value");
            if (valProp != null)
            {
                var raw = valProp.GetValue(navTimeValue);
                if (raw is DateTime rawDt) dt = rawDt;
            }
        }
        catch { }

        if (!dt.HasValue) return null;

        // Parse the format string, resolving time tokens
        bool hasTimeToken = false;
        var result = new System.Text.StringBuilder();
        int i = 0;
        while (i < formatString.Length)
        {
            if (formatString[i] == '<')
            {
                int end = formatString.IndexOf('>', i);
                if (end < 0)
                {
                    result.Append(formatString[i]);
                    i++;
                    continue;
                }

                string token = formatString.Substring(i + 1, end - i - 1);
                string? resolved = ResolveAlTimeToken(dt.Value, token);
                if (resolved != null)
                {
                    hasTimeToken = true;
                    result.Append(resolved);
                }
                else
                {
                    // Unknown token — preserve as-is
                    result.Append('<');
                    result.Append(token);
                    result.Append('>');
                }
                i = end + 1;
            }
            else
            {
                result.Append(formatString[i]);
                i++;
            }
        }

        return hasTimeToken ? result.ToString() : null;
    }

    /// <summary>
    /// Resolve a single AL time format token like "Hours24,2", "Minutes,2", "Seconds,2".
    /// Returns null if the token is not a recognised time token.
    /// </summary>
    private static string? ResolveAlTimeToken(DateTime dt, string token)
    {
        string name;
        int width = 0;
        int commaPos = token.IndexOf(',');
        if (commaPos >= 0)
        {
            name = token.Substring(0, commaPos).Trim();
            int.TryParse(token.Substring(commaPos + 1).Trim(), out width);
        }
        else
        {
            name = token.Trim();
        }

        return name.ToLowerInvariant() switch
        {
            "hours24" => width > 0 ? dt.Hour.ToString($"D{width}") : dt.Hour.ToString(),
            "hours12" => (dt.Hour % 12 == 0 ? 12 : dt.Hour % 12).ToString(width > 0 ? $"D{width}" : ""),
            "minutes" => width > 0 ? dt.Minute.ToString($"D{width}") : dt.Minute.ToString(),
            "seconds" => width > 0 ? dt.Second.ToString($"D{width}") : dt.Second.ToString(),
            _ => null, // Not a time token
        };
    }

    private static string FormatDecimal(decimal d)
    {
        // AL Format() shows whole numbers without decimals, always using invariant (dot) decimal separator
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return d == Math.Truncate(d) ? d.ToString("0", culture) : d.ToString("0.##########", culture);
    }

    /// <summary>
    /// Extracts and formats a NavTime value without using NavSession-dependent formatters.
    /// NavTime stores the time as a DateTime internally (date part is meaningless).
    /// We extract the time-of-day via the Value property.
    /// </summary>
    private static string FormatNavTime(object value)
    {
        try
        {
            var valProp = value.GetType().GetProperty("Value");
            if (valProp != null)
            {
                var raw = valProp.GetValue(value);
                if (raw is DateTime dt)
                    return dt.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        catch { }

        // Fallback: try the internal 'value' field
        try
        {
            var field = value.GetType().GetField("value",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field != null)
            {
                var raw = field.GetValue(value);
                if (raw is DateTime dt)
                    return dt.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        catch { }

        return "00:00:00";
    }

    // NavVariant type-check properties (rewritten from value.ALIsXxx to AlCompat.ALIsXxx(value))
    // The rewriter passes the MockVariant object directly, so each method must
    // unwrap MockVariant before checking the underlying value type.
    // HasTypeName also handles NAV runtime wrapper types (NavBoolean, NavInteger, etc.)
    // that appear when values originate from record fields rather than AL literals.
    private static object? UnwrapVariant(object? v) => v is MockVariant mv ? mv.Value : v;
    private static bool HasTypeName(object? v, string typeName) => v?.GetType().Name == typeName;
    public static bool ALIsBoolean(object? v) { v = UnwrapVariant(v); return v is bool || HasTypeName(v, "NavBoolean"); }
    public static bool ALIsOption(object? v) { v = UnwrapVariant(v); return v is Enum || HasTypeName(v, "NavOption"); }
    public static bool ALIsInteger(object? v) { v = UnwrapVariant(v); return v is int || HasTypeName(v, "NavInteger"); }
    public static bool ALIsByte(object? v) { v = UnwrapVariant(v); return v is byte; }
    public static bool ALIsBigInteger(object? v) { v = UnwrapVariant(v); return v is long || HasTypeName(v, "NavBigInteger"); }
    public static bool ALIsDecimal(object? v) { v = UnwrapVariant(v); return v is decimal || HasTypeName(v, "Decimal18") || HasTypeName(v, "NavDecimal"); }
    public static bool ALIsText(object? v) { v = UnwrapVariant(v); return v is string || HasTypeName(v, "NavText"); }
    public static bool ALIsCode(object? v) { v = UnwrapVariant(v); return HasTypeName(v, "NavCode"); }
    public static bool ALIsChar(object? v) { v = UnwrapVariant(v); return v is char; }
    public static bool ALIsTextConst(object? v) { v = UnwrapVariant(v); return HasTypeName(v, "NavTextConstant"); }
    public static bool ALIsDate(object? v) { v = UnwrapVariant(v); return (v is DateTime dt && dt.TimeOfDay == TimeSpan.Zero) || HasTypeName(v, "NavDate"); }
    public static bool ALIsTime(object? v) { v = UnwrapVariant(v); return HasTypeName(v, "NavTime"); }
    public static bool ALIsDuration(object? v) { v = UnwrapVariant(v); return v is TimeSpan; }
    public static bool ALIsDateTime(object? v) { v = UnwrapVariant(v); return v is DateTime || HasTypeName(v, "NavDateTime"); }
    public static bool ALIsDateFormula(object? v) { v = UnwrapVariant(v); return HasTypeName(v, "NavDateFormula"); }
    public static bool ALIsGuid(object? v) { v = UnwrapVariant(v); return v is Guid || HasTypeName(v, "NavGuid"); }
    public static bool ALIsRecordId(object? v) { v = UnwrapVariant(v); return v?.GetType().Name == "NavRecordId"; }
    public static bool ALIsRecord(object? v) { v = UnwrapVariant(v); return v is MockRecordHandle || v?.GetType().Name.StartsWith("Record") == true; }
    public static bool ALIsRecordRef(object? v) { v = UnwrapVariant(v); return v is MockRecordRef || v?.GetType().Name == "NavRecordRef"; }
    public static bool ALIsFieldRef(object? v) { v = UnwrapVariant(v); return v is MockFieldRef || v?.GetType().Name == "NavFieldRef"; }
    public static bool ALIsCodeunit(object? v) { v = UnwrapVariant(v); return v?.GetType().Name.StartsWith("Codeunit") == true; }
    public static bool ALIsFile(object? v) { v = UnwrapVariant(v); return v?.GetType().Name == "NavFile"; }
    public static bool ALIsDotNet(object? v) => false; // DotNet types not supported in standalone
    public static bool ALIsAutomation(object? v) => false; // Automation types not supported in standalone

    /// <summary>
    /// Replacement for ALCompiler.ToSecretText(navText).
    /// Converts a NavText (or string) to NavSecretText.
    /// The BC version goes through NavSession; our version constructs directly.
    /// </summary>
    public static NavSecretText ToSecretText(object? value)
    {
        if (value is NavSecretText st) return st;
        if (value is NavText nt) return NavSecretText.Create((string)nt);
        if (value is string s) return NavSecretText.Create(s);
        return NavSecretText.Create(value?.ToString() ?? "");
    }

    /// <summary>
    /// Safe NavCode constructor that pre-uppercases the string value.
    /// NavCode.EnsureValueIsUppercasedIfNeeded() calls NavEnvironment which crashes on Linux.
    /// By passing an already-uppercased string, the check is skipped.
    /// </summary>
    public static NavCode CreateNavCode(int maxLength, string value)
    {
        return new NavCode(maxLength, value?.ToUpperInvariant() ?? "");
    }

    // -----------------------------------------------------------------------
    // ALSystemNumeric replacements (ALRandomize/ALRandom require NavSession)
    // -----------------------------------------------------------------------
    [ThreadStatic] private static Random? _random;

    /// <summary>
    /// Replacement for ALSystemNumeric.ALRandomize(seed) which requires NavSession.
    /// Seeds the thread-local random number generator.
    /// </summary>
    public static void ALRandomize(int seed)
    {
        _random = new Random(seed);
    }

    /// <summary>
    /// Replacement for ALSystemNumeric.ALRandomize() (no-arg) which requires NavSession.
    /// Seeds the thread-local random number generator with a time-based seed.
    /// </summary>
    public static void ALRandomize()
    {
        _random = new Random();
    }

    /// <summary>
    /// Replacement for ALSystemNumeric.ALRandom(maxNumber) which requires NavSession.
    /// Returns a random integer in [1, maxNumber].
    /// </summary>
    public static int ALRandom(int maxNumber)
    {
        _random ??= new Random();
        if (maxNumber <= 0) return 0;
        return _random.Next(1, maxNumber + 1);
    }

    // -----------------------------------------------------------------------
    // GUID creation helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Replacement for ALDatabase.ALCreateGuid() which requires NavSession.
    /// Returns a new random GUID wrapped as NavGuid.
    /// </summary>
    public static NavGuid ALCreateGuid() => new NavGuid(Guid.NewGuid());

    /// <summary>
    /// Replacement for ALDatabase.ALCreateSequentialGuid() which requires NavSession.
    /// BC's sequential GUID algorithm is opaque; we return a random GUID which is
    /// sufficient for all test purposes (uniqueness, non-empty assertions).
    /// </summary>
    public static NavGuid ALCreateSequentialGuid() => new NavGuid(Guid.NewGuid());

    /// <summary>
    /// Replacement for ALDatabase.ALIsNullGuid(g) which requires NavSession.
    /// Returns true when the GUID is the all-zeros default ({00000000-...}).
    /// </summary>
    public static NavBoolean ALIsNullGuid(object? g)
    {
        g = UnwrapVariant(g);
        if (g is NavGuid ng) return NavBoolean.Create(ng.ToGuid() == Guid.Empty);
        if (g is Guid guid) return NavBoolean.Create(guid == Guid.Empty);
        return NavBoolean.Create(true); // null/unset treated as empty
    }

    // -----------------------------------------------------------------------
    // HttpContent stream helpers
    // -----------------------------------------------------------------------
    // After the NavHttpContent→MockHttpContent and NavInStream→MockInStream
    // type renames in the rewriter, calls to ALLoadFrom(MockInStream) and
    // ALReadAs(ITreeObject, DataError, ByRef<MockInStream>) are redirected
    // to AlCompat helpers that accept the mock types.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Replacement for MockHttpContent.ALLoadFrom(MockInStream).
    /// BC emits content.ALLoadFrom(inStream.Value) for HttpContent.WriteFrom(InStream).
    /// Reads all available text from the mock stream and loads it as UTF-8 content.
    /// </summary>
    public static void HttpContentLoadFrom(MockHttpContent content, MockInStream stream)
    {
        content.ALLoadFrom(new NavText(stream.ReadAll()));
    }

    /// <summary>
    /// Passthrough for MockHttpContent.ALLoadFrom(NavText) — text variant of
    /// HttpContent.WriteFrom(Text) still routes through here after the rewriter
    /// redirect so the same AlCompat.HttpContentLoadFrom name handles both overloads.
    /// </summary>
    public static void HttpContentLoadFrom(MockHttpContent content, NavText text)
        => content.ALLoadFrom(text);

    /// <summary>
    /// Replacement for MockHttpContent.ALReadAs(ITreeObject, DataError, ByRef&lt;MockInStream&gt;).
    /// BC emits content.ALReadAs(this, DataError.ThrowError, stream) for
    /// HttpContent.ReadAs(var Stream: InStream). Returns a MockInStream whose data
    /// is the stored text content (round-trip from WriteFrom).
    /// Note: This is a text-only round-trip. Binary data written via InStream will be
    /// UTF-8 decoded on load and re-encoded on read, which may not preserve raw bytes.
    /// </summary>
    public static void HttpContentReadAs(MockHttpContent content, object? scope, DataError errorLevel, ByRef<MockInStream> stream)
    {
        var text = content.GetText();
        if (string.IsNullOrEmpty(text))
        {
            stream.Value = new MockInStream();
        }
        else
        {
            var ms = new MockInStream();
            ms.Init(System.Text.Encoding.UTF8.GetBytes(text));
            stream.Value = ms;
        }
    }

    /// <summary>
    /// Session.ApplicationArea() stub — returns empty string in standalone mode.
    /// </summary>
    public static string ApplicationArea() => "";

    /// <summary>
    /// Session.GetExecutionContext() / GetModuleExecutionContext() stub.
    /// Returns ExecutionContext.Normal in standalone mode.
    /// </summary>
    public static Microsoft.Dynamics.Nav.Types.ExecutionContext GetExecutionContext() => Microsoft.Dynamics.Nav.Types.ExecutionContext.Normal;

    /// <summary>
    /// CompanyProperty.DisplayName() stub — returns a default company name.
    /// </summary>
    public static string CompanyPropertyDisplayName() => "My Company";

    /// <summary>
    /// CompanyProperty.UrlName() stub — returns a URL-encoded company name.
    /// </summary>
    public static string CompanyPropertyUrlName() => "My%20Company";

    /// <summary>
    /// NormalDate(date) — wraps ALSystemDate.ALNormalDate with 0D handling.
    /// BC runtime throws NavNCLDateInvalidException on 0D; we return 0D.
    /// </summary>
    public static NavDate NormalDate(NavDate date)
    {
        if (date == NavDate.Default) return NavDate.Default;
        return ALSystemDate.ALNormalDate(date);
    }

    /// <summary>
    /// ClosingDate(date) — wraps ALSystemDate.ALClosingDate with 0D handling.
    /// </summary>
    public static NavDate ClosingDate(NavDate date)
    {
        if (date == NavDate.Default) return NavDate.Default;
        return ALSystemDate.ALClosingDate(date);
    }

    /// <summary>
    /// RoundDateTime(dt) — rounds to nearest 1000ms (default precision).
    /// </summary>
    public static NavDateTime RoundDateTime(NavDateTime dt)
    {
        return RoundDateTime(dt, 1000, "=");
    }

    /// <summary>
    /// RoundDateTime(dt, precision) — rounds to nearest with given precision in ms.
    /// </summary>
    public static NavDateTime RoundDateTime(NavDateTime dt, long precision)
    {
        return RoundDateTime(dt, precision, "=");
    }

    /// <summary>
    /// RoundDateTime(dt, precision, direction) — rounds a DateTime value.
    /// Precision is in milliseconds. Direction: '>' (up), '&lt;' (down), '=' (nearest).
    /// All NavDateTime access uses reflection to avoid BC 28+ Telemetry.Abstractions
    /// loading — the == operator, explicit (DateTime) cast, and Create() methods all
    /// trigger assembly resolution that fails outside the service tier.
    /// </summary>
    public static NavDateTime RoundDateTime(NavDateTime dt, long precision, string direction)
    {
        var dateTime = GetNavDateTimeValue(dt);
        if (dateTime == default) return NavDateTime.Default;
        if (precision <= 0) precision = 1;

        long ticksPrecision = precision * TimeSpan.TicksPerMillisecond;
        long ticks = dateTime.Ticks;
        long remainder = ticks % ticksPrecision;

        long diffTicks;
        switch (direction)
        {
            case ">":
                diffTicks = remainder == 0 ? 0 : ticksPrecision - remainder;
                break;
            case "<":
                diffTicks = -remainder;
                break;
            default: // "=" or nearest
                diffTicks = remainder >= ticksPrecision / 2
                    ? ticksPrecision - remainder
                    : -remainder;
                break;
        }

        if (diffTicks == 0) return dt;

        return CreateNavDateTime(new DateTime(ticks + diffTicks, dateTime.Kind));
    }

    // Cache the backing field for NavDateTime construction via reflection.
    // NavDateTime.Create(DateTime) and operator+(Int64) both trigger loading of
    // Telemetry.Abstractions in BC 28+, which is unavailable outside the service tier.
    // Constructing via Activator + field set bypasses all such dependencies.
    private static readonly System.Reflection.FieldInfo? NavDateTimeValueField =
        typeof(NavDateTime).BaseType?.GetField("value",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    internal static NavDateTime CreateNavDateTime(DateTime dateTime)
    {
        var result = (NavDateTime)System.Activator.CreateInstance(typeof(NavDateTime), nonPublic: true)!;
        NavDateTimeValueField?.SetValue(result, dateTime);
        return result;
    }

    /// <summary>
    /// Read the backing DateTime value from a NavDateTime via reflection.
    /// Avoids using the explicit (DateTime) cast operator which triggers
    /// Telemetry.Abstractions loading in BC 28+.
    /// </summary>
    internal static DateTime GetNavDateTimeValue(NavDateTime dt)
    {
        if (NavDateTimeValueField == null) return default;
        var val = NavDateTimeValueField.GetValue(dt);
        return val is DateTime d ? d : default;
    }
}
