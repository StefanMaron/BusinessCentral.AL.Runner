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

    /// <summary>
    /// Per-scope-type last statement hit — maps scope class name to the last statement ID
    /// executed in that scope on the current thread. Used by FormatStackFrames to resolve
    /// AL line numbers for each frame in the call stack.
    /// </summary>
    [ThreadStatic] private static Dictionary<string, int>? _scopeLastStmtId;

    private static Dictionary<string, int> ScopeLastStmtId =>
        _scopeLastStmtId ??= new Dictionary<string, int>();

    public static (string TypeName, int Id)? LastStatementHit => _lastStatementHit;

    public static void ResetLastStatement()
    {
        _lastStatementHit = null;
        _scopeLastStmtId = null;
    }

    /// <summary>Set the last statement from another thread (propagate ThreadStatic state).</summary>
    public static void SetLastStatement(string typeName, int id) => _lastStatementHit = (typeName, id);

    /// <summary>Get the last statement ID recorded for a given scope class name.</summary>
    public static int? GetLastStmtForScope(string typeName)
    {
        if (_scopeLastStmtId != null && _scopeLastStmtId.TryGetValue(typeName, out var id))
            return id;
        return null;
    }

    /// <summary>
    /// Capture per-scope tracking state for cross-thread propagation.
    /// Returns a snapshot of the current thread's scope tracking dictionary.
    /// </summary>
    public static Dictionary<string, int>? GetScopeTracking() =>
        _scopeLastStmtId != null ? new Dictionary<string, int>(_scopeLastStmtId) : null;

    /// <summary>
    /// Restore per-scope tracking state propagated from another thread.
    /// Called on the receiving thread after a background test thread exits.
    /// </summary>
    public static void SetScopeTracking(Dictionary<string, int>? tracking) =>
        _scopeLastStmtId = tracking;

    protected void StmtHit(int n)
    {
        var typeName = GetType().Name;
        _hitStatements.Add((typeName, n));
        _lastStatementHit = (typeName, n);
        ScopeLastStmtId[typeName] = n;
        IterationTracker.RecordHit(n);
        BreakpointManager.CheckHit(typeName, n);
    }

    protected bool CStmtHit(int n)
    {
        var typeName = GetType().Name;
        _hitStatements.Add((typeName, n));
        _lastStatementHit = (typeName, n);
        ScopeLastStmtId[typeName] = n;
        IterationTracker.RecordHit(n);
        BreakpointManager.CheckHit(typeName, n);
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
            LastErrorObject = null;
        }
        catch (Exception ex)
        {
            // Unwrap TargetInvocationException from reflection calls
            var inner = ex;
            while (inner is System.Reflection.TargetInvocationException tie && tie.InnerException != null)
                inner = tie.InnerException;
            LastErrorText = inner.Message;
            LastErrorObject = new MockVariant(inner.Message);
        }
    }

    /// <summary>
    /// Stores the last error message from asserterror blocks.
    /// </summary>
    public static string LastErrorText { get; set; } = "";

    /// <summary>
    /// Stores the last error object from asserterror blocks.
    /// BC emits <c>ALSystemErrorHandling.ALGetLastErrorObject(parent)</c> for
    /// <c>GetLastErrorObject()</c>. In standalone mode we return a MockVariant
    /// containing the error message text, or null when no error is active.
    /// </summary>
    public static MockVariant? LastErrorObject { get; set; } = null;

    /// <summary>
    /// Returns the last error object as a MockVariant. Returns a default empty
    /// MockVariant when no error is active (ClearLastError was called or no
    /// asserterror has fired yet).
    /// </summary>
    public static MockVariant GetLastErrorObject()
        => LastErrorObject ?? new MockVariant("");

    /// <summary>
    /// Resets both LastErrorText and LastErrorObject to their cleared state.
    /// BC emits <c>ALSystemErrorHandling.ALClearLastError()</c> for <c>ClearLastError()</c>.
    /// Rewriter rewrites that call to <c>AlScope.ClearLastErrorState()</c>.
    /// </summary>
    public static void ClearLastErrorState()
    {
        LastErrorText = "";
        LastErrorObject = null;
    }

    // ── WorkDate ─────────────────────────────────────────────────────────────
    // AL WorkDate() returns the session's "working date". In standalone mode,
    // defaults to NavDate.Default (0D) which tests can override per-test via WorkDate(D).

    private static NavDate _workDate = NavDate.Default;

    /// <summary>WorkDate getter — returns the configured work date.</summary>
    public static NavDate GetWorkDate() => _workDate;

    /// <summary>WorkDate setter — stores the date for use within this test.</summary>
    public static void SetWorkDate(NavDate date) => _workDate = date;

    /// <summary>
    /// Stores the last error code. Always empty in standalone runner (BC error codes
    /// require structured ErrorInfo which is beyond runner scope).
    /// </summary>
    public static string LastErrorCode { get; set; } = "";

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

    /// <summary>
    /// Instance <c>this</c>-returning stub for <c>Parent</c> — issues #1092, #1105, #1111.
    ///
    /// Some BC compiler versions emit <c>this.Parent</c> (instance access) in
    /// generated scope class code when a scope class body references a parent
    /// codeunit field. Without this instance property, the generated C# fails with:
    ///   CS0176: 'AlScope.Parent' cannot be accessed with an instance reference.
    ///
    /// Earlier versions (issue #1092) emitted a static class-qualified access that
    /// was fixed with a static property, but that caused CS0176 for the instance
    /// pattern. The correct fix is an instance (virtual) property so that both
    /// <c>this.Parent</c> and <c>base.Parent</c> compile and resolve correctly.
    ///
    /// The real parent reference is always accessed through the injected instance
    /// property on the concrete scope subclass (e.g. <c>public Codeunit123 Parent => _parent;</c>)
    /// which shadows this base stub.
    ///
    /// Returning <c>this</c> (rather than null) prevents a runtime null-dereference when
    /// BC emits <c>ALSession.ALBindSubscription(DataError, base.Parent)</c> inside a scope
    /// class and the rewriter rewrites this to <c>base.Parent.Bind()</c> but does NOT
    /// further rewrite <c>base.Parent</c> to <c>_parent</c> (because the new node is not
    /// re-visited — issue #1111).  With <c>Parent => this</c>, <c>base.Parent.Bind()</c>
    /// calls <c>AlScope.Bind()</c> (the no-op stub) instead of throwing
    /// "Cannot perform runtime binding on a null reference".
    ///
    /// Returning <c>dynamic</c> instead of <c>object</c> allows calls such as
    /// <c>base.Parent.Bind()</c> to compile via dynamic dispatch.  Concrete scope
    /// classes shadow this with a strongly-typed property so the dynamic dispatch
    /// penalty only affects the rare unresolved case.
    /// </summary>
    public virtual dynamic Parent => this;

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

    /// <summary>
    /// Zero-arg overload emitted by BC compiler for GetCollectedErrors() in AL.
    /// Returns the collected errors without clearing (BC default behaviour).
    /// </summary>
    public static NavList<NavALErrorInfo> GetCollectedErrors()
        => GetCollectedErrors(clearErrors: false);

    /// <summary>Clears all collected errors.</summary>
    public static void ClearCollectedErrors() => CollectedErrors.Clear();

    /// <summary>Reset collected errors state between tests.</summary>
    public static void ResetCollectedErrors()
    {
        _isCollectingErrors = false;
        _collectedErrors = null;
    }

    /// <summary>
    /// Runs <paramref name="body"/> with <see cref="IsCollectingErrors"/> set to true,
    /// then restores the previous state.  Called by the test executor when the test
    /// procedure itself is annotated with [ErrorBehavior(ErrorBehavior::Collect)].
    /// </summary>
    public static void RunWithCollecting(Action body)
    {
        bool was = _isCollectingErrors;
        _isCollectingErrors = true;
        try { body(); }
        finally { _isCollectingErrors = was; }
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
    /// Bind — no-op stub on AlScope base.
    ///
    /// BC emits <c>ALSession.ALBindSubscription(DataError, base.Parent)</c> in scope
    /// classes. The rewriter converts this to <c>base.Parent.Bind()</c> → <c>_parent.Bind()</c>.
    /// The concrete codeunit class gets its own <c>Bind()</c> injected by the rewriter
    /// (see <c>isCodeunitClass</c> block); this base virtual stub covers edge-cases
    /// where the enclosing type is not a codeunit — issue #1106 / Gap 2.
    /// </summary>
    public virtual void Bind() { }

    /// <summary>
    /// Unbind — no-op stub on AlScope base (symmetric to <see cref="Bind"/>).
    /// </summary>
    public virtual void Unbind() { }

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
/// In standalone mode, all operations are no-ops or return sensible defaults.
/// </summary>
public class MockCurrPage
{
    public bool Editable { get; set; }
    // BC compiler maps CurrPage.Caption → C# property PageCaption.
    // The setter receives a C# string literal, so string is the backing type.
    public string PageCaption { get; set; } = string.Empty;
    public bool LookupMode { get; set; }
    /// <summary>BC compiler maps CurrPage.PromptMode → C# property PromptMode (NavOption = Enum "Prompt Mode").</summary>
    public NavOption? PromptMode { get; set; }

    /// <summary>
    /// CurrPage.ObjectId(UseCaptionName) — returns the page's object identifier.
    /// BC compiler maps CurrPage.ObjectId → C# ObjectID (capital D).
    /// In standalone mode there is no running page, so return an empty string.
    /// </summary>
    public NavText ObjectID(bool useCaptionName) => NavText.Empty;

    /// <summary>
    /// CurrPage.SetSelectionFilter(var Rec) — applies the UI row selection as
    /// a record filter.  In standalone mode this is a no-op.
    /// </summary>
    public void SetSelectionFilter(MockRecordHandle rec) { }

    public void Update(bool saveRecord = true) { }
    public void Close() { }
    public void Activate() { }
    public void SaveRecord() { }

    // ── Background task API ──────────────────────────────────────────────────

    /// <summary>
    /// CurrPage.EnqueueBackgroundTask — BC emits this on the CurrPage instance.
    /// In standalone mode, there is no page session: set taskId to a stub value (1)
    /// and return. No actual background task is dispatched.
    /// </summary>
    public void EnqueueBackgroundTask(DataError errorLevel, ByRef<int> taskId, int codeunitId)
        => taskId.Value = 1;

    public void EnqueueBackgroundTask(DataError errorLevel, ByRef<int> taskId, int codeunitId,
        NavDictionary<NavText, NavText> parameters)
        => taskId.Value = 1;

    public void EnqueueBackgroundTask(DataError errorLevel, ByRef<int> taskId, int codeunitId, int timeout)
        => taskId.Value = 1;

    public void EnqueueBackgroundTask(DataError errorLevel, ByRef<int> taskId, int codeunitId,
        NavDictionary<NavText, NavText> parameters, int timeout)
        => taskId.Value = 1;

    /// <summary>
    /// CurrPage.CancelBackgroundTask — no-op in standalone mode (task already ran synchronously).
    /// </summary>
    public void CancelBackgroundTask(int taskId) { }
    public void CancelBackgroundTask(DataError errorLevel, int taskId) { }

    /// <summary>
    /// CurrPage.GetPart(partHash) — used by page extension code when accessing subpages
    /// via <c>CurrPage.SubPart.Page.SomeProcedure()</c>.
    /// BC lowers this to <c>CurrPage.GetPart(hash).CreateNavFormHandle(scope).Invoke(methodHash, args)</c>.
    /// Returns a <see cref="MockPagePartHandle"/> that searches all Page* types for the method.
    /// </summary>
    public MockPagePartHandle GetPart(int partHash) => new MockPagePartHandle(partHash);
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
    public void ALUpdate(int fieldNo, NavText value) { }
    /// <summary>
    /// Explicit NavCode overload to resolve CS0121 ambiguity when a Code field value
    /// is passed to Dialog.Update. NavCode extends NavValue AND has an implicit string
    /// conversion, so both ALUpdate(int, NavValue) and ALUpdate(int, string) match —
    /// causing CS0121. This overload provides an exact match, eliminating the ambiguity.
    /// </summary>
    public void ALUpdate(int fieldNo, NavCode value) { }
    public void ALClose() { }
    public void ALAssign(MockDialog other) { }

    /// <summary>
    /// AL's Clear(dlg) — rewriter emits dlg.Clear(). Resets the dialog to its
    /// default (unopen) state. No-op standalone: there is no real UI to dismiss.
    /// </summary>
    public void Clear() { }

    /// <summary>
    /// AL's Dialog.HideSubsequentDialogs — emitted as a property setter by the
    /// BC compiler (`dlg.ALHideSubsequentDialogs = true`). No UI standalone;
    /// the set is a no-op.
    /// </summary>
    public bool ALHideSubsequentDialogs { get; set; }

    /// <summary>
    /// AL's Dialog.LogInternalError(msg, dataClass, verbosity) — static method in AL.
    /// BC emits `MockDialog.ALLogInternalError(session, msg, DataClassification, Verbosity)`
    /// with a leading session (null! standalone). No telemetry pipeline standalone;
    /// no-op stub. `object?` parameter types accept NavText/string + enum ordinals.
    /// </summary>
    public static void ALLogInternalError(object? session, object? msg, object? dataClassification, object? verbosity) { }

    /// <summary>
    /// AL's StrMenu(options [, defaultNo [, caption]]) — displays a menu and
    /// returns the 1-based index of the selected option (or 0 on cancel).
    /// BC lowers StrMenu to static calls on MockDialog with a leading session
    /// (null! standalone) and a trailing Guid (dialog-id token).
    ///
    /// No UI standalone — we return the `defaultNo` (0 when omitted), matching
    /// the "unhandled dialog → use default or cancel" convention already used
    /// for CONFIRM and MESSAGE without handlers.
    /// </summary>
    public static int ALStrMenu(object? session, object? options, Guid dialogId) => 0;

    public static int ALStrMenu(object? session, object? options, int defaultNo, Guid dialogId) => defaultNo;

    public static int ALStrMenu(object? session, object? options, int defaultNo, object? caption, Guid dialogId) => defaultNo;

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
        // In BC, Error('') performs a silent transaction rollback without
        // showing an error message. It's used as a cancel pattern (e.g. when
        // a Confirm dialog returns false). In standalone mode we can't roll
        // back, but we should not propagate it as a test failure.
        if (string.IsNullOrEmpty(format)) return;

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
            // Store a shallow clone so later mutations to the same ErrorInfo object
            // (common in BC-generated code that reuses one local variable for multiple errors)
            // do not overwrite already-collected messages.
            var cloneMethod = typeof(object).GetMethod("MemberwiseClone",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var snapshot = (NavALErrorInfo)(cloneMethod!.Invoke(errorInfo, null) ?? errorInfo);
            AlScope.CollectedErrors.Add(snapshot);
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
    ///
    /// Also validates the ordinal against the registry when the enum is
    /// registered (non-extensible, declared in user AL sources). This
    /// catches invalid ordinals from <c>Enum::"T".FromInteger(I)</c> calls,
    /// which BC emits using this same pattern. Validation is skipped when
    /// the registry has no members for the enum (extensible or external enum).
    /// </summary>
    public static NavOption CreateTaggedOption(int enumObjectId, int ordinal)
    {
        // Only validate for Extensible = false enums — extensible enums may have
        // extension ordinals not present in the registry, so validation is unsafe.
        if (EnumRegistry.IsNonExtensible(enumObjectId))
        {
            var members = EnumRegistry.GetMembers(enumObjectId);
            if (members.Count > 0)
            {
                bool valid = false;
                foreach (var (v, _) in members)
                    if (v == ordinal) { valid = true; break; }
                if (!valid)
                    throw new Exception($"The value {ordinal} is not a valid ordinal for this enum type.");
            }
        }
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

        FireEventInAssembly(asm, objectType, publisherId, eventName, eventArgs);

        // Also fire events from dependency assemblies
        if (MockCodeunitHandle.DependencyAssemblies != null)
        {
            foreach (var depAsm in MockCodeunitHandle.DependencyAssemblies)
                FireEventInAssembly(depAsm, objectType, publisherId, eventName, eventArgs);
        }
    }

    private static void FireEventInAssembly(System.Reflection.Assembly asm, int objectType, int publisherId, string eventName, object?[] eventArgs)
    {
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

            // Automatic subscribers: reuse SingleInstance or create fresh
            object? instance;
            try
            {
                instance = MockCodeunitHandle.GetSingleInstance(ownerType);
                if (instance == null)
                {
                    instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(ownerType);
                    AlCompat.InitializeUninitializedObject(instance);
                    MockCodeunitHandle.CacheSingleInstanceIfNeeded(ownerType, instance);
                }
            }
            catch { continue; }

            InvokeSubscriber(instance, method, eventArgs);
        }
    }

    // Open generic type for ByRef<T> from the BC runtime.
    // Used in InvokeSubscriber to detect when a subscriber parameter is ByRef<T>
    // and the event arg is a plain T — e.g. "var RunTrigger: Boolean" compiles
    // to ByRef<bool> but FireImplicitDbEvent passes a raw bool.
    private static readonly System.Type? _byRefOpenGeneric = typeof(ByRef<bool>).GetGenericTypeDefinition();

    /// <summary>
    /// Invoke an event subscriber method, coercing each argument to the
    /// declared parameter type.  The most common mismatch is a plain value
    /// being passed for a <c>ByRef&lt;T&gt;</c> parameter (e.g. a subscriber
    /// that declares <c>var RunTrigger: Boolean</c> where BC emits
    /// <c>ByRef&lt;bool&gt;</c> but <see cref="MockRecordHandle.FireImplicitDbEvent"/>
    /// passes a plain <c>bool</c>).
    ///
    /// When the arg count is less than the parameter count (subscriber declares
    /// extra optional-style params) the remaining args stay null; reference-type
    /// params accept null, and value-type params are left as default(T).
    /// </summary>
    private static void InvokeSubscriber(object? instance, System.Reflection.MethodInfo method, object?[] eventArgs)
    {
        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];
        for (int i = 0; i < args.Length; i++)
        {
            var raw = i < eventArgs.Length ? eventArgs[i] : null;
            args[i] = CoerceArgForParameter(parameters[i], raw);
        }
        try { method.Invoke(instance, args); }
        catch (System.Reflection.TargetInvocationException tie)
        {
            if (tie.InnerException != null) throw tie.InnerException;
            throw;
        }
    }

    /// <summary>
    /// Coerce <paramref name="arg"/> so it is assignable to the declared
    /// <paramref name="param"/> type.
    ///
    /// <list type="bullet">
    ///   <item><c>ByRef&lt;T&gt;</c> param + plain <c>T</c> arg — wraps the value
    ///   in a <c>ByRef&lt;T&gt;</c> using a getter/setter backed by a local
    ///   variable so mutation inside the subscriber is captured (though the
    ///   writeback currently only matters to the subscriber itself, not to the
    ///   caller, since implicit DB event firers do not use the returned value).</item>
    ///   <item>Value-type param + null arg — returns <c>Activator.CreateInstance(T)</c>
    ///   (the default value) so reflection does not throw on the unboxing.</item>
    ///   <item>All other cases — returns <paramref name="arg"/> unchanged.</item>
    /// </list>
    /// </summary>
    private static object? CoerceArgForParameter(System.Reflection.ParameterInfo param, object? arg)
    {
        var paramType = param.ParameterType;

        // ByRef<T> wrapping: subscriber declared "var X: SomeType" for a
        // value or non-record type, so BC emits ByRef<T>.  The event fires
        // with a plain T — we wrap it.
        if (_byRefOpenGeneric != null
            && paramType.IsGenericType
            && paramType.GetGenericTypeDefinition() == _byRefOpenGeneric)
        {
            // If arg is already ByRef<T>, pass it through.
            if (arg != null && paramType.IsInstanceOfType(arg))
                return arg;

            var innerType = paramType.GetGenericArguments()[0];
            // Build a ByRef<T> using its (Func<T>, Action<T>) constructor.
            // We use a single-element object array as the backing store so that
            // mutations inside the subscriber are captured even though we don't
            // propagate them back to the original eventArgs.
            var store = new object?[] { arg };
            try
            {
                // Build Func<T>: () => (T)store[0]
                var funcType = typeof(System.Func<>).MakeGenericType(innerType);
                var actionType = typeof(System.Action<>).MakeGenericType(innerType);

                // Use a generic helper to avoid per-type Activator overheads.
                var helperMethod = typeof(AlCompat).GetMethod(
                    nameof(MakeByRef),
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                    ?.MakeGenericMethod(innerType);

                if (helperMethod != null)
                    return helperMethod.Invoke(null, new[] { store });
            }
            catch { /* fall through — return arg as-is and let Invoke give a clearer error */ }

            return arg;
        }

        // Value-type param with null arg: supply the type default so Invoke
        // does not throw "Object of type 'null' cannot be converted to type T".
        if (arg == null && paramType.IsValueType)
            return System.Activator.CreateInstance(paramType);

        return arg;
    }

    /// <summary>
    /// Generic helper that creates a <c>ByRef&lt;T&gt;</c> backed by
    /// <paramref name="store"/>[0] via getter/setter lambdas.
    /// Called reflectively from <see cref="CoerceArgForParameter"/> to avoid
    /// generating per-type IL at runtime.
    /// </summary>
    internal static ByRef<T> MakeByRef<T>(object?[] store)
    {
        return new ByRef<T>(
            () => store[0] is T v ? v : default!,
            v => store[0] = v);
    }

    /// <summary>
    /// Implements <c>Enum::"T".FromInteger(I)</c> — validates <paramref name="ordinal"/>
    /// against the declared members in <see cref="EnumRegistry"/> and returns a tagged
    /// NavOption.  Throws if the ordinal is not declared.
    /// Emitted by the rewriter for <c>NCLEnumMetadata.Create(N).FromInteger(I)</c>.
    /// If the enum is not in the registry (e.g. an extensible/external enum), validation
    /// is skipped (same fallback as <see cref="GetEnumOrdinals"/>).
    /// </summary>
    public static NavOption EnumFromInteger(int enumObjectId, int ordinal)
    {
        // Delegate to CreateTaggedOption which validates for Extensible = false enums.
        return CreateTaggedOption(enumObjectId, ordinal);
    }

    /// <summary>Overload for Decimal18 — AL Integer variables are Decimal18 in BC's C# output.</summary>
    public static NavOption EnumFromInteger(int enumObjectId, Decimal18 ordinal)
        => EnumFromInteger(enumObjectId, (int)ordinal);

    /// <summary>
    /// Validates <paramref name="ordinal"/> against the compile-time-inlined
    /// <paramref name="validOrdinals"/> array and returns a tagged NavOption.
    /// When the valid ordinals are known at rewrite time (non-extensible enums
    /// declared in the same compilation), the rewriter inlines them to avoid
    /// depending on <see cref="EnumRegistry"/> state at runtime.
    /// </summary>
    public static NavOption EnumFromIntegerValidated(int enumObjectId, int ordinal, int[] validOrdinals)
    {
        bool valid = false;
        for (int i = 0; i < validOrdinals.Length; i++)
            if (validOrdinals[i] == ordinal) { valid = true; break; }
        if (!valid)
            throw new Exception($"The value {ordinal} is not a valid ordinal for this enum type.");
        return CreateTaggedOption(enumObjectId, ordinal);
    }

    /// <summary>Overload for Decimal18 — AL Integer variables are Decimal18 in BC's C# output.</summary>
    public static NavOption EnumFromIntegerValidated(int enumObjectId, Decimal18 ordinal, int[] validOrdinals)
        => EnumFromIntegerValidated(enumObjectId, (int)ordinal, validOrdinals);

    /// <summary>
    /// Create a NavOption that inherits the enum-id tag from an existing
    /// NavOption. Emitted by the rewriter for
    /// <c>NavOption.Create(existing.NavOptionMetadata, V)</c>
    /// reassignments so the new instance keeps its enum-id lineage.
    /// </summary>
    public static NavOption CloneTaggedOption(NavOption existing, int ordinal)
    {
        // Guard: when an AL enum/option field or variable has never been assigned,
        // the underlying NavOption reference at the C# level is null.  The BC
        // compiler emits NavOption.Create(existing.NavOptionMetadata, V) which
        // the rewriter transforms to CloneTaggedOption(existing, V).  With a null
        // existing, ConditionalWeakTable.TryGetValue throws
        // "Value cannot be null (Parameter 'key')".  Treat null as an
        // uninitialized option and just create a fresh NavOption — BC behaviour
        // is that uninitialized option fields default to ordinal 0.
        if (existing == null)
            return AlRunner.Runtime.MockRecordHandle.CreateOptionValue(ordinal);

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
    /// Replacement for ALCompiler.ObjectToNavOutStream on the chained-call path.
    /// BC emits ALCompiler.ObjectToNavOutStream(parent, expr) when an expression
    /// that returns OutStream is used directly (chained), e.g.:
    ///   TempBlob.CreateOutStream().WriteText(...)
    /// After the rewriter renames NavOutStream → MockOutStream in type identifiers,
    /// the method invocation is still wired to ALCompiler.ObjectToNavOutStream which
    /// returns NavOutStream. This replacement returns MockOutStream instead.
    /// </summary>
    public static MockOutStream ObjectToMockOutStream(object? parent, object? value)
    {
        if (value is MockOutStream mockOutStream)
            return mockOutStream;

        // Fallback: return an empty stream rather than throw, so callers that
        // just chain a write-and-discard don't crash.
        return MockOutStream.Default();
    }

    /// <summary>
    /// Replacement for ALCompiler.ObjectToNavInStream on the chained-call path.
    /// See ObjectToMockOutStream for the symmetric explanation.
    /// </summary>
    public static MockInStream ObjectToMockInStream(object? parent, object? value)
    {
        if (value is MockInStream mockInStream)
            return mockInStream;

        return MockInStream.Default();
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
        if (value is MockVariant mv)
        {
            if (mv.Value is T mvValue) return mvValue;
            // Unwrap variant and retry
            return NavIndirectValueToNavValue<T>(mv.Value);
        }
        if (value is NavValue nv && nv is T typedValue) return typedValue;
        // Coerce bool/NavBoolean → NavText using AL's "Yes"/"No" representation
        if (typeof(T) == typeof(NavText))
        {
            if (value is bool boolVal)
                return (T)(NavValue)new NavText(boolVal ? "Yes" : "No");
            if (value is NavBoolean nb)
                return (T)(NavValue)new NavText((bool)nb ? "Yes" : "No");
            return (T)(NavValue)new NavText(Format(value));
        }
        throw new InvalidCastException($"Cannot convert {value?.GetType().Name ?? "null"} to {typeof(T).Name}");
    }

    /// <summary>
    /// 2-argument overload of NavIndirectValueToNavValue for the BC compiler's
    /// <c>ALCompiler.NavIndirectValueToNavValue&lt;T&gt;(value, metadata)</c> pattern.
    /// The metadata argument (NavValueDefinedLengthMetadata) is ignored — only the
    /// value conversion matters in standalone mode.
    /// </summary>
    public static T NavIndirectValueToNavValue<T>(object? value, object? metadata) where T : NavValue
        => NavIndirectValueToNavValue<T>(value);

    /// <summary>
    /// Safe replacement for ALCompiler.ObjectToExactNavValue&lt;T&gt;(x).
    /// The rewriter converts <c>ALCompiler.ObjectToExactNavValue&lt;T&gt;(x)</c> to
    /// <c>(T)(object)(x)</c>, which fails at runtime when x is a C# primitive (bool, int)
    /// that cannot be directly cast to the BC NavValue type T.
    /// This helper handles the common coercions, matching BC's implicit conversions.
    /// Note: T is unconstrained so it also handles NavVariant → MockVariant cases.
    /// </summary>
    public static T ObjectToExactNavValue<T>(object? value)
    {
        if (value is T direct) return direct;
        // Unwrap MockVariant for NavValue targets
        if (value is MockVariant mv && typeof(T) != typeof(MockVariant))
            return ObjectToExactNavValue<T>(mv.Value);
        // bool/NavBoolean → NavText: AL uses "Yes"/"No" representation
        if (typeof(T) == typeof(NavText))
        {
            if (value is bool boolVal)
                return (T)(object)new NavText(boolVal ? "Yes" : "No");
            if (value is NavBoolean nb)
                return (T)(object)new NavText((bool)nb ? "Yes" : "No");
            if (value is NavValue src)
                return (T)(object)new NavText(Format(src));
            return (T)(object)new NavText(Format(value));
        }
        // Fallback: try direct cast (may throw for truly incompatible types, matching BC behaviour)
        return (T)(object)value!;
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
        // MockVersion — Format(ver) returns "major.minor.build.revision"
        if (value is MockVersion mver) return $"{mver.ALMajor}.{mver.ALMinor}.{mver.ALBuild}.{mver.ALRevision}";
        // Handle native .NET numeric types
        if (value is decimal d) return FormatDecimal(d);
        if (value is double dbl) return FormatDecimal((decimal)dbl);
        if (value is float f) return FormatDecimal((decimal)f);
        if (value is int or long or short or byte) return value.ToString()!;
        // System.Boolean — BC scopes declare Boolean locals as C# bool. AL Format(true)="Yes", Format(false)="No".
        if (value is bool boolV) return boolV ? "Yes" : "No";
        // System.Guid — BC 26.x compiles AL Guid variables as System.Guid (not NavGuid).
        // Default AL format is "B" = {XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX} (38 chars, uppercase).
        if (value is Guid sysGuid) return sysGuid.ToString("B").ToUpperInvariant();
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
        // NavGuid — check by type name (version-independent) and extract the Guid value.
        // Pattern-match on Microsoft.Dynamics.Nav.Runtime.NavGuid is unreliable across BC versions.
        // Try multiple extraction strategies; last resort parses ToString() which NavGuid always
        // returns in "D" (36-char) format. Format "B" = {XXXXXXXX-...} (38 chars, braces).
        if (typeName == "NavGuid")
        {
            try
            {
                // Try parameterless ToGuid() method
                var toGuidMethod = value.GetType().GetMethod("ToGuid", Type.EmptyTypes);
                if (toGuidMethod != null)
                    return ((Guid)toGuidMethod.Invoke(value, null)!).ToString("B").ToUpperInvariant();
            }
            catch { }
            try
            {
                // Try Value property of type Guid
                var valueProp = value.GetType().GetProperty("Value");
                if (valueProp?.PropertyType == typeof(Guid))
                    return ((Guid)valueProp.GetValue(value)!).ToString("B").ToUpperInvariant();
            }
            catch { }
            // Last resort: NavGuid.ToString() always returns a parseable Guid string ("D" format).
            if (Guid.TryParse(value.ToString(), out var parsedGuid))
                return parsedGuid.ToString("B").ToUpperInvariant();
        }
        // Handle NavValue subtypes — use ToText() where available, avoid ToString() which may need NavSession
        if (value is Microsoft.Dynamics.Nav.Runtime.NavValue nv)
        {
            try
            {
                if (value is Microsoft.Dynamics.Nav.Runtime.NavText nt) return (string)nt;
                if (value is Microsoft.Dynamics.Nav.Runtime.NavBoolean nb) return (bool)nb ? "Yes" : "No";
                if (value is Microsoft.Dynamics.Nav.Runtime.NavInteger ni) return ((int)ni).ToString();
                if (value is Microsoft.Dynamics.Nav.Runtime.NavBigInteger nbi) return ((long)nbi).ToString();
                // NavByte.ToText() — BCL routes through NCLManagedAdapter.ByteToTextChar (OEM native code)
                // which fails without the BC service tier. Return the numeric string (0-255) instead.
                if (typeName == "NavByte")
                {
                    try
                    {
                        var valProp = value.GetType().GetProperty("Value");
                        if (valProp != null) return valProp.GetValue(value)?.ToString() ?? "";
                    }
                    catch { }
                    return "";
                }
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
    /// AL PadStr(String, Length, Filler): pad or truncate to fixed width.
    /// Positive Length = right-pad (append filler). Negative Length = left-pad (prepend filler).
    /// If |Length| &lt;= source length, result is source truncated to |Length| (no padding added).
    /// The real BC ALSystemString.ALPadStr rejects negative Length with NavNCLOutsidePermittedRangeException,
    /// so we route through AlCompat to implement AL's documented behavior.
    /// </summary>
    public static string PadStr(string? source, int length, string? filler)
    {
        var src = source ?? "";
        var fillCh = string.IsNullOrEmpty(filler) ? ' ' : filler![0];
        int absLen = length < 0 ? -length : length;
        if (src.Length >= absLen) return src.Substring(0, absLen);
        int padCount = absLen - src.Length;
        var pad = new string(fillCh, padCount);
        return length < 0 ? pad + src : src + pad;
    }

    public static string PadStr(string? source, int length) => PadStr(source, length, " ");

    /// <summary>
    /// Format with AL format string (e.g. '&lt;Year4&gt;-&lt;Month,2&gt;-&lt;Day,2&gt;').
    /// The BC transpiler emits NavFormatEvaluateHelper.Format(session, value, length, formatString)
    /// which the rewriter strips the session arg from, producing AlCompat.Format(value, length, formatString).
    /// </summary>
    public static string Format(object? value, int length, string formatString)
    {
        if (!string.IsNullOrEmpty(formatString) && formatString.Contains('<'))
        {
            // Handle NavBoolean with <Standard Format,N>: format 2 → "1"/"0", others → "Yes"/"No"
            var unwrapped = value is MockVariant mv2 ? mv2.Value : value;
            if (unwrapped is Microsoft.Dynamics.Nav.Runtime.NavBoolean nb2)
            {
                bool boolVal = (bool)nb2;
                var stdMatch = System.Text.RegularExpressions.Regex.Match(
                    formatString, @"<Standard Format,(\d+)>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (stdMatch.Success && int.TryParse(stdMatch.Groups[1].Value, out int fmt))
                    return fmt == 2 ? (boolVal ? "1" : "0") : (boolVal ? "Yes" : "No");
                return boolVal ? "Yes" : "No";
            }

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
        if (value is int i) return i;
        if (value is long l) return l;
        // BC numeric types via explicit cast operators
        if (value is Microsoft.Dynamics.Nav.Runtime.NavInteger ni) return (decimal)(int)ni;
        if (value is Microsoft.Dynamics.Nav.Runtime.NavBigInteger nbi) return (decimal)(long)nbi;
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
    public static bool ALIsCodeunit(object? v) { v = UnwrapVariant(v); return v is MockCodeunitHandle || v?.GetType().Name.StartsWith("Codeunit") == true; }
    public static bool ALIsFile(object? v) { v = UnwrapVariant(v); return v?.GetType().Name == "NavFile"; }
    public static bool ALIsDotNet(object? v) => false;
    public static bool ALIsAutomation(object? v) => false;

    // JSON Is* — checks NavJsonToken and its subclasses (NavJsonObject, NavJsonArray, NavJsonValue)
    public static bool ALIsJsonToken(object? v) { v = UnwrapVariant(v); return v is NavJsonToken; }
    public static bool ALIsJsonObject(object? v) { v = UnwrapVariant(v); return v is NavJsonToken && v.GetType().Name == "NavJsonObject"; }
    public static bool ALIsJsonArray(object? v) { v = UnwrapVariant(v); return v is NavJsonToken && v.GetType().Name == "NavJsonArray"; }
    public static bool ALIsJsonValue(object? v) { v = UnwrapVariant(v); return v is NavJsonToken && v.GetType().Name == "NavJsonValue"; }

    // Stream Is* — checks mock types used in standalone mode
    public static bool ALIsInStream(object? v) { v = UnwrapVariant(v); return v is MockInStream; }
    public static bool ALIsOutStream(object? v) { v = UnwrapVariant(v); return v is MockOutStream; }

    // Notification, TextBuilder, List
    public static bool ALIsNotification(object? v) { v = UnwrapVariant(v); return v is MockNotification; }
    public static bool ALIsTextBuilder(object? v) { v = UnwrapVariant(v); return v is MockTextBuilder; }
    public static bool ALIsList(object? v) { v = UnwrapVariant(v); return v != null && v.GetType().IsGenericType && (v.GetType().GetGenericTypeDefinition().FullName?.StartsWith("Microsoft.Dynamics.Nav.Runtime.NavList", StringComparison.Ordinal) == true); }
    public static bool ALIsDictionary(object? v) { v = UnwrapVariant(v); return v != null && v.GetType().IsGenericType && (v.GetType().GetGenericTypeDefinition().FullName?.StartsWith("Microsoft.Dynamics.Nav.Runtime.NavDictionary", StringComparison.Ordinal) == true); }

    // Misc stubs — no mock types in standalone mode
    public static bool ALIsAction(object? v) => false;
    public static bool ALIsBinary(object? v) => false;
    public static bool ALIsClientType(object? v) => false;
    public static bool ALIsDataClassification(object? v) => false;
    public static bool ALIsDataClassificationType(object? v) => false;
    public static bool ALIsDefaultLayout(object? v) => false;
    public static bool ALIsExecutionMode(object? v) => false;
    public static bool ALIsFilterPageBuilder(object? v) => false;
    public static bool ALIsObjectType(object? v) => false;
    public static bool ALIsPromptMode(object? v) => false;
    public static bool ALIsReportFormat(object? v) => false;
    public static bool ALIsSecurityFiltering(object? v) => false;
    public static bool ALIsTableConnectionType(object? v) => false;
    public static bool ALIsTestPermissions(object? v) => false;
    public static bool ALIsTextConstant(object? v) => false;
    public static bool ALIsTextEncoding(object? v) => false;
    public static bool ALIsTransactionType(object? v) => false;
    public static bool ALIsWideChar(object? v) => false;

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

    /// <summary>
    /// NavCode equality comparison that avoids NavEnvironment.
    /// NavCode.op_Equality and NavCode.ToString() both call NavEnvironment
    /// which crashes in standalone mode. The explicit <c>(string)</c> cast uses
    /// NavCode.op_Explicit which extracts the internal string value directly
    /// (same pattern used in MockRecordHandle and MockFieldRef).
    /// </summary>
    public static bool NavCodeEquals(NavCode a, NavCode b)
        => string.Equals((string)a, (string)b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// NavCode ordering comparison that avoids NavEnvironment.
    /// BC emits <c>Category.CompareTo(new NavCode(N, "A"))</c> for
    /// <c>case Category of 'A':</c> statements. <c>NavCode.CompareTo</c>
    /// calls <c>NavStringValue.CompareTo</c> which calls NavEnvironment
    /// (null in standalone → NullReferenceException). This helper does the
    /// same comparison via the same safe <c>(string)</c> cast used in
    /// <see cref="NavCodeEquals"/>.
    /// </summary>
    public static int NavCodeCompare(NavCode a, NavCode b)
        => string.Compare((string)a, (string)b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// NavValue → NavCode coercing overloads for <see cref="NavCodeCompare"/>.
    /// BC emits <c>new InListNavValue((candidate) =&gt; NavCodeCompare(candidate, new NavCode(...)) == 0)</c>
    /// for <c>Rec."CodeField" in ['A', 'B']</c>. The lambda parameter is typed
    /// <c>NavValue</c> (the delegate's signature), but the arguments are NavCode literals,
    /// so the call without overloads fails with CS1503. These overloads extract the
    /// underlying string via the same path used elsewhere (NavCode is the expected
    /// runtime type; any other NavValue is compared by its Format representation).
    /// Issue #1211.
    /// </summary>
    public static int NavCodeCompare(NavValue a, NavCode b)
        => string.Compare(NavValueToCodeString(a), (string)b, StringComparison.OrdinalIgnoreCase);

    public static int NavCodeCompare(NavCode a, NavValue b)
        => string.Compare((string)a, NavValueToCodeString(b), StringComparison.OrdinalIgnoreCase);

    public static int NavCodeCompare(NavValue a, NavValue b)
        => string.Compare(NavValueToCodeString(a), NavValueToCodeString(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>NavValue → NavCode coercing overloads for <see cref="NavCodeEquals"/>. See #1211.</summary>
    public static bool NavCodeEquals(NavValue a, NavCode b)
        => string.Equals(NavValueToCodeString(a), (string)b, StringComparison.OrdinalIgnoreCase);

    public static bool NavCodeEquals(NavCode a, NavValue b)
        => string.Equals((string)a, NavValueToCodeString(b), StringComparison.OrdinalIgnoreCase);

    public static bool NavCodeEquals(NavValue a, NavValue b)
        => string.Equals(NavValueToCodeString(a), NavValueToCodeString(b), StringComparison.OrdinalIgnoreCase);

    private static string NavValueToCodeString(NavValue v)
    {
        if (v is null) return "";
        if (v is NavCode nc) return (string)nc;
        if (v is NavText nt) return (string)nt;
        return Format(v)?.ToString() ?? "";
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

    /// <summary>
    /// Replacement for ALSystemNumeric.ALRound(v) single-arg — the BC SDK's 1-arg
    /// overload defaults precision to 0 (no rounding), which diverges from AL's
    /// documented behaviour where Round(v) rounds to the nearest integer.
    /// Uses AwayFromZero so 3.5 -> 4 and -3.5 -> -4, matching AL semantics.
    /// </summary>
    public static Decimal18 ALRound(Decimal18 v)
    {
        decimal rounded = Math.Round((decimal)v, 0, MidpointRounding.AwayFromZero);
        return new Decimal18(rounded);
    }

    // -----------------------------------------------------------------------
    // ALSystemArray replacements (CompressArray / CopyArray)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns true if the value is "blank" for AL CompressArray purposes.
    /// For Text/Code types: blank = empty string. For all others: blank = default(T).
    /// </summary>
    private static bool IsBlankArrayElement<T>(T item)
    {
        if (item is null) return true;
        if (item is NavText nt) return nt.ToString() == "";
        if (item is NavCode nc) return nc.ToString() == "";
        return EqualityComparer<T>.Default.Equals(item, default(T)!);
    }

    /// <summary>
    /// Returns the blank/default value for type T in AL CompressArray context.
    /// </summary>
    private static T GetBlankArrayElement<T>(MockArray<T> arr)
    {
        // Grab the last element's "blank" form by checking the type
        if (typeof(T) == typeof(NavText)) return (T)(object)new NavText("");
        if (typeof(T) == typeof(NavCode)) return (T)(object)new NavCode(0, "");
        return default(T)!;
    }

    /// <summary>
    /// Replacement for ALSystemArray.ALCompressArray&lt;T&gt;(NavArray&lt;T&gt;) which
    /// requires NavArray (needs ITreeObject). Shifts all non-blank elements toward
    /// the beginning of the array, filling the tail with blank/default values.
    /// BC semantics: blank = empty string for Text/Code, 0 for numeric types.
    /// </summary>
    public static void ALCompressArray<T>(MockArray<T> arr)
    {
        T blank = GetBlankArrayElement(arr);
        int writeIdx = 0;
        for (int i = 0; i < arr.Length; i++)
        {
            if (!IsBlankArrayElement(arr[i]))
                arr[writeIdx++] = arr[i];
        }
        while (writeIdx < arr.Length)
            arr[writeIdx++] = blank;
    }

    /// <summary>
    /// Replacement for ALSystemArray.ALCopyArray&lt;T&gt;(NavArray&lt;T&gt;, NavArray&lt;T&gt;, int, int).
    /// Copies <paramref name="count"/> elements from <paramref name="src"/> starting at
    /// 1-based <paramref name="fromIndex"/> into the beginning of <paramref name="dest"/>.
    /// BC emits the fromIndex as-is (1-based) matching AL CopyArray(Dest, Src, FromIdx[, Count]).
    /// </summary>
    public static void ALCopyArray<T>(MockArray<T> dest, MockArray<T> src, int fromIndex, int count)
    {
        int srcStart = fromIndex - 1;  // AL 1-based → C# 0-based
        for (int i = 0; i < count; i++)
            dest[i] = src[srcStart + i];
    }

    /// <summary>
    /// 3-arg overload of <see cref="ALCopyArray{T}"/>: copies all remaining elements
    /// from <paramref name="src"/> starting at 1-based <paramref name="fromIndex"/>
    /// into the beginning of <paramref name="dest"/>.
    /// Equivalent to <c>CopyArray(Dest, Src, FromIndex)</c> in AL (no Count argument).
    /// </summary>
    public static void ALCopyArray<T>(MockArray<T> dest, MockArray<T> src, int fromIndex)
    {
        int count = src.Length - fromIndex + 1;  // all elements from fromIndex to end
        ALCopyArray(dest, src, fromIndex, count);
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
    /// Replacement for ALDatabase.ALUserSecurityId() which requires NavSession.
    /// Returns a fixed non-null Guid stable across reads within a process so tests
    /// that compare two reads observe equality. The exact value is arbitrary; what
    /// matters is that it is non-null and stable.
    /// </summary>
    private static readonly Guid _userSecurityId =
        new Guid("22222222-2222-2222-2222-222222222222");

    public static Guid UserSecurityId() => _userSecurityId;

    /// <summary>
    /// Replacement for ALDatabase.ALSID() which requires NavSession.
    /// Returns a fixed non-real SID string (not a valid Windows domain SID).
    /// Stable across calls within a process so tests that compare two reads observe equality.
    /// </summary>
    public static string DatabaseSID() => "S-1-0-0";

    /// <summary>
    /// Replacement for ALDatabase.ALCurrentTransactionType() which requires NavSession.
    /// The runner has no real transaction tracking; returns TransactionType.Update,
    /// the most common real-world value and a predictable stable stub.
    /// TransactionType is a BC enum: Browse=0, Snapshot=1, Update=2, Report=3.
    /// </summary>
    public static Microsoft.Dynamics.Nav.Types.TransactionType ALCurrentTransactionType()
        => Microsoft.Dynamics.Nav.Types.TransactionType.Update;

    /// <summary>
    /// Guid.ToText([withBraces]) — BC returns uppercase GUID with braces by default.
    /// withBraces=true  → {XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX} (38 chars, "B" format, uppercase)
    /// withBraces=false → XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX      (32 chars, "N" format, uppercase)
    /// Called for all navGuid.ALToText([bool]) invocations (RoslynRewriter intercepts them).
    /// MockTextBuilder is also routed here and delegates back to its own ALToText().
    /// </summary>
    public static NavText GuidToText(object? g, bool withBraces)
    {
        g = UnwrapVariant(g);
        var format = withBraces ? "B" : "N";
        if (g is NavGuid ng) return new NavText(ng.ToGuid().ToString(format).ToUpperInvariant());
        if (g is Guid guid) return new NavText(guid.ToString(format).ToUpperInvariant());
        // MockTextBuilder.ALToText() is also routed here — delegate back to preserve correct text.
        if (g is MockTextBuilder mtb) return mtb.ALToText();
        // NavGuid from a different BC version assembly: use reflection or Guid.TryParse as fallback.
        if (g != null && g.GetType().Name == "NavGuid")
        {
            try
            {
                var toGuidMethod = g.GetType().GetMethod("ToGuid", Type.EmptyTypes);
                if (toGuidMethod != null)
                    return new NavText(((Guid)toGuidMethod.Invoke(g, null)!).ToString(format).ToUpperInvariant());
            }
            catch { }
            try
            {
                var valueProp = g.GetType().GetProperty("Value");
                if (valueProp?.PropertyType == typeof(Guid))
                    return new NavText(((Guid)valueProp.GetValue(g)!).ToString(format).ToUpperInvariant());
            }
            catch { }
            if (Guid.TryParse(g.ToString(), out var parsed))
                return new NavText(parsed.ToString(format).ToUpperInvariant());
        }
        return new NavText(Format(g)); // fallback for non-Guid types
    }

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

    /// <summary>
    /// Stub for ALDatabase.ALHasTableConnection(TableConnectionType, Name).
    /// The runner has no real external table connections, so always returns false.
    /// </summary>
    public static bool HasTableConnection(TableConnectionType tableConnectionType, string name) => false;

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
    /// Overload for <c>HttpContent.WriteFrom(SecretText)</c>.
    /// BC emits <c>AlCompat.HttpContentLoadFrom(content, NavSecretText)</c> after
    /// the ALLoadFrom redirect — resolving the <c>NavSecretText → MockInStream</c>
    /// type mismatch (#1086). In standalone mode secrets are treated as plain text:
    /// the value is unwrapped and stored as UTF-8 text content.
    /// </summary>
    public static void HttpContentLoadFrom(MockHttpContent content, NavSecretText secret)
        => content.ALLoadFrom(Unwrap(secret));

    /// <summary>
    /// Replacement for MockHttpContent.ALReadAs(ITreeObject, DataError, ByRef&lt;MockInStream&gt;).
    /// BC emits content.ALReadAs(this, DataError.ThrowError, stream) for
    /// HttpContent.ReadAs(var Stream: InStream). Returns a MockInStream whose data
    /// is the stored text content (round-trip from WriteFrom).
    /// Note: This is a text-only round-trip. Binary data written via InStream will be
    /// UTF-8 decoded on load and re-encoded on read, which may not preserve raw bytes.
    ///
    /// Returns <c>true</c> (matching the real BC API) so that BC-generated code that
    /// uses <c>if Content.ReadAs(Stream) then</c> compiles without CS0019 (#1250).
    /// </summary>
    public static bool HttpContentReadAs(MockHttpContent content, object? scope, DataError errorLevel, ByRef<MockInStream> stream)
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
        return true;
    }

    // -----------------------------------------------------------------------
    // XmlDocument.ReadFrom(InStream, ...) — CS1503 after NavInStream→MockInStream rename.
    // BC emits NavXmlDocument.ALReadFrom(DataError, NavInStream, ByRef<NavXmlDocument>) for the
    // InStream overload, and NavXmlDocument.ALReadFrom(DataError, NavText, ByRef<NavXmlDocument>)
    // for the Text overload. After NavInStream→MockInStream rewrite the InStream form breaks
    // because NavXmlDocument only has a string overload in BC's DLL.
    // The rewriter redirects ALL NavXmlDocument.ALReadFrom calls here; both string and
    // MockInStream variants are handled via overloads.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Replacement for <c>NavXmlDocument.ALReadFrom(DataError, InStream, ByRef&lt;NavXmlDocument&gt;)</c>.
    /// AL: <c>XmlDocument.ReadFrom(InStream, var Document)</c>.
    /// Reads all text from the stream and delegates to the BC string overload.
    /// </summary>
    public static bool XmlDocumentReadFrom(DataError errorLevel, MockInStream stream, ByRef<NavXmlDocument> document)
    {
        string text = stream.ReadAll();
        return NavXmlDocument.ALReadFrom(errorLevel, text, document);
    }

    /// <summary>
    /// Passthrough for <c>NavXmlDocument.ALReadFrom(DataError, NavText, ByRef&lt;NavXmlDocument&gt;)</c>.
    /// AL: <c>XmlDocument.ReadFrom(Text, var Document)</c>.
    /// Text form routes here after the rewriter redirect so the same helper name covers both overloads.
    /// </summary>
    public static bool XmlDocumentReadFrom(DataError errorLevel, NavText text, ByRef<NavXmlDocument> document)
        => NavXmlDocument.ALReadFrom(errorLevel, text, document);

    /// <summary>
    /// Passthrough for BC-emitted string literals: <c>NavXmlDocument.ALReadFrom(DataError, "...", ByRef&lt;NavXmlDocument&gt;)</c>.
    /// BC emits string literals as C# <c>string</c>; this overload prevents ambiguity between
    /// <c>NavText</c> and <c>MockInStream</c> overloads.
    /// </summary>
    public static bool XmlDocumentReadFrom(DataError errorLevel, string text, ByRef<NavXmlDocument> document)
        => NavXmlDocument.ALReadFrom(errorLevel, text, document);

    /// <summary>
    /// Replacement for <c>NavXmlDocument.ALReadFrom(DataError, InStream, XmlReadOptions, ByRef&lt;NavXmlDocument&gt;)</c>.
    /// AL: <c>XmlDocument.ReadFrom(InStream, Options, var Document)</c>.
    /// Reads all text from the stream and delegates to the BC string+options overload.
    /// </summary>
    public static bool XmlDocumentReadFrom(DataError errorLevel, MockInStream stream, NavXmlReadOptions options, ByRef<NavXmlDocument> document)
    {
        string text = stream.ReadAll();
        return NavXmlDocument.ALReadFrom(errorLevel, text, options, document);
    }

    /// <summary>
    /// Passthrough for <c>NavXmlDocument.ALReadFrom(DataError, NavText, XmlReadOptions, ByRef&lt;NavXmlDocument&gt;)</c>.
    /// AL: <c>XmlDocument.ReadFrom(Text, Options, var Document)</c>.
    /// </summary>
    public static bool XmlDocumentReadFrom(DataError errorLevel, NavText text, NavXmlReadOptions options, ByRef<NavXmlDocument> document)
        => NavXmlDocument.ALReadFrom(errorLevel, text, options, document);

    /// <summary>
    /// Passthrough for BC-emitted string literals with options:
    /// <c>NavXmlDocument.ALReadFrom(DataError, "...", XmlReadOptions, ByRef&lt;NavXmlDocument&gt;)</c>.
    /// </summary>
    public static bool XmlDocumentReadFrom(DataError errorLevel, string text, NavXmlReadOptions options, ByRef<NavXmlDocument> document)
        => NavXmlDocument.ALReadFrom(errorLevel, text, options, document);

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
    /// CompanyProperty.DisplayName() — returns the configurable company display name.
    /// Defaults to "My Company"; override via <c>AL Runner Config</c> → <c>SetCompanyDisplayName()</c>.
    /// </summary>
    public static string CompanyPropertyDisplayName() => MockSession.GetCompanyDisplayName();

    /// <summary>
    /// CompanyProperty.UrlName() — returns the configurable URL-encoded company name.
    /// Defaults to "My%20Company"; override via <c>AL Runner Config</c> → <c>SetCompanyUrlName()</c>.
    /// </summary>
    public static string CompanyPropertyUrlName() => MockSession.GetCompanyUrlName();

    /// <summary>
    /// CompanyProperty.ID() — returns the configurable company GUID.
    /// Defaults to a fixed non-empty GUID; override via <c>AL Runner Config</c> → <c>SetCompanyId()</c>.
    /// BC lowers this to ALCompanyProperty.ALID() which requires NavEnvironment.
    /// </summary>
    public static NavGuid CompanyPropertyID() => new NavGuid(MockSession.GetCompanyId());

    // ── Media stubs ──────────────────────────────────────────────────────────

    /// <summary>
    /// GetDocumentUrl(MediaId) stub — no BC Media service in standalone mode.
    /// Returns empty string. BC lowers to NavMedia.ALGetDocumentUrl(mediaId).
    /// </summary>
    public static string GetDocumentUrl(object? mediaId) => "";

    /// <summary>
    /// ImportStreamWithUrlAccess(InStream, FileName, Duration) stub — no BC Media service in standalone mode.
    /// Returns an empty GUID (BC lowers the return value as Guid → Text via ALCompiler.GuidToNavText).
    /// BC lowers to NavMedia.ALImportWithUrlAccess(stream, filename, duration).
    /// </summary>
    public static System.Guid ImportStreamWithUrlAccess(MockInStream stream, string filename, int duration) => System.Guid.Empty;

    // ── Caption class stub ───────────────────────────────────────────────────

    /// <summary>
    /// CaptionClassTranslate(CaptionExpression) stub — no caption class service in standalone mode.
    /// Returns the input expression unchanged. BC lowers to ALSystemObject.ALCaptionClassTranslate(expr).
    /// </summary>
    public static string CaptionClassTranslate(string expr) => expr ?? "";

    /// <summary>NavText overload for CaptionClassTranslate.</summary>
    public static string CaptionClassTranslate(NavText expr) => CaptionClassTranslate((string)expr);

    // ── Date/Variant conversion helpers ─────────────────────────────────────
    // BC emits ALSystemDate.ALDMY2Date(session, ...) / ALVariant2Date(session, ...)
    // etc. with a NavMethodScope first arg. AlScope is not NavMethodScope, so the
    // rewriter strips the session arg and redirects here.

    /// <summary>DMY2Date(day, month, year) — construct a date from components.</summary>
    public static NavDate DMY2Date(int day, int month, int year)
        => ALSystemDate.ALDMY2Date(null!, day, month, year);

    /// <summary>DWY2Date(day, week, year) — construct a date from ISO week components.</summary>
    public static NavDate DWY2Date(int day, int week, int year)
        => ALSystemDate.ALDWY2Date(null!, day, week, year);

    /// <summary>
    /// Variant2Date — extract a NavDate from a MockVariant.
    /// BC emits ALSystemDate.ALVariant2Date(null!, v) but v is already MockVariant
    /// after the rewriter replaces NavVariant; we unwrap and return the NavDate inside.
    /// </summary>
    public static NavDate Variant2Date(MockVariant v)
    {
        var raw = v?.Value;
        if (raw is NavDate d) return d;
        return NavDate.Default;
    }

    /// <summary>
    /// Variant2Time — extract a NavTime from a MockVariant.
    /// </summary>
    public static NavTime Variant2Time(MockVariant v)
    {
        var raw = v?.Value;
        if (raw is NavTime t) return t;
        return NavTime.Default;
    }

    /// <summary>
    /// CreateDateTime(date, time) — wrap a NavDate + NavTime into a NavDateTime.
    /// BC emits ALSystemDate.ALCreateDateTime(session, d, t); the rewriter strips
    /// the session and redirects here so we can apply the wall-clock → UTC
    /// conversion BC's read-side ConvertTimeFromUtc expects.
    /// </summary>
    public static NavDateTime CreateDateTime(NavDate d, NavTime t)
        => BuildNavDateTimeUtc(d, t);

    /// <summary>
    /// DaTi2Variant(date, time) — pack a NavDate + NavTime into a NavDateTime MockVariant.
    /// BC emits ALSystemDate.ALDaTi2Variant(scope, d, t); the rewriter strips the scope
    /// and redirects here. Symmetric with CreateDateTime so BC's read path inverts the
    /// write correctly on any host.
    /// </summary>
    public static MockVariant DaTi2Variant(NavDate d, NavTime t)
        => new(BuildNavDateTimeUtc(d, t));

    private static NavDateTime BuildNavDateTimeUtc(NavDate d, NavTime t)
        => CreateNavDateTime(SafeLocalToUtc(CombineDateTime(d, t), TimeZoneInfo.Local));

    /// Combine date and time components into a wall-clock DateTime with
    /// DateTimeKind.Unspecified. Uses reflection on the backing value field because
    /// NavDate / NavTime do not implement IConvertible outside a NavSession.
    private static DateTime CombineDateTime(NavDate d, NavTime t)
    {
        var date = NavDateValueField is null ? DateTime.MinValue
            : (NavDateValueField.GetValue(d) as DateTime?) ?? DateTime.MinValue;
        var time = NavTimeValueField is null ? DateTime.MinValue
            : (NavTimeValueField.GetValue(t) as DateTime?) ?? DateTime.MinValue;
        return new DateTime(
            date.Year, date.Month, date.Day,
            time.Hour, time.Minute, time.Second, time.Millisecond,
            DateTimeKind.Unspecified);
    }

    private static System.Reflection.FieldInfo? GetNavBackingValueField(Type navValueType)
        => navValueType.BaseType?.GetField("value",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private static readonly System.Reflection.FieldInfo? NavDateValueField =
        GetNavBackingValueField(typeof(NavDate));

    private static readonly System.Reflection.FieldInfo? NavTimeValueField =
        GetNavBackingValueField(typeof(NavTime));

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
    /// CalcDate(formula, date) — wraps ALSystemDate.ALCalcDate with null-session handling.
    /// BC runtime throws NavNCLDateInvalidException when the session is null on some
    /// platforms (Windows). We try the real BC call first, then fall back to .NET date
    /// arithmetic if it throws. String-formula overload.
    /// </summary>
    public static NavDate CalcDate(string formula, NavDate date)
    {
        if (date == NavDate.Default)
            throw new Exception("You cannot base a date calculation on an undefined date.\n\nDate: 0D\nFormula: " + (formula ?? "") + ".");
        try
        {
            return ALSystemDate.ALCalcDate(null!, formula, date);
        }
        catch (Exception ex) when (ex.GetType().Name.Contains("NavNCL") || ex is NullReferenceException)
        {
            return CalcDateFallback(formula, date);
        }
    }

    /// <summary>
    /// CalcDate(formula, date) — DateFormula overload.
    /// Extracts the formula string from NavDateFormula and delegates to the string overload.
    /// </summary>
    public static NavDate CalcDate(NavDateFormula formula, NavDate date)
    {
        if (date == NavDate.Default)
            throw new Exception("You cannot base a date calculation on an undefined date.\n\nDate: 0D\nFormula: " + (formula?.ToString() ?? "") + ".");
        try
        {
            return ALSystemDate.ALCalcDate(null!, formula, date);
        }
        catch (Exception ex) when (ex.GetType().Name.Contains("NavNCL") || ex is NullReferenceException)
        {
            // Extract the formula string from NavDateFormula via its ToString()
            var formulaStr = formula.ToString() ?? "";
            // NavDateFormula.ToString() may not include angle brackets; ensure they are present
            if (!string.IsNullOrEmpty(formulaStr) && !formulaStr.StartsWith("<"))
                formulaStr = "<" + formulaStr + ">";
            return CalcDateFallback(formulaStr, date);
        }
    }

    /// <summary>
    /// Fallback CalcDate implementation using .NET date arithmetic.
    /// Parses common BC date formula patterns: +/-NnD, +/-NnW, +/-NnM, +/-NnQ, +/-NnY,
    /// CD, CW, CM, CQ, CY, and combinations thereof.
    /// </summary>
    private static NavDate CalcDateFallback(string formula, NavDate date)
    {
        if (date == NavDate.Default)
            throw new Exception("You cannot base a date calculation on an undefined date.");

        var dateVal = NavDateValueField is null ? DateTime.MinValue
            : (NavDateValueField.GetValue(date) as DateTime?) ?? DateTime.MinValue;

        if (dateVal == DateTime.MinValue)
            throw new Exception("You cannot base a date calculation on an undefined date.");

        // Strip angle brackets if present
        var f = formula.Trim();
        if (f.StartsWith("<") && f.EndsWith(">"))
            f = f.Substring(1, f.Length - 2);

        var result = ApplyDateFormula(f, dateVal);
        return NavDate.Create((uint)((result.Year * 10000) + (result.Month * 100) + result.Day));
    }

    /// <summary>
    /// Parse and apply a date formula string (without angle brackets) to a DateTime.
    /// Supports: [+/-]N{D|W|M|Q|Y}, C{D|W|M|Q|Y}, and combinations.
    /// </summary>
    private static DateTime ApplyDateFormula(string formula, DateTime baseDate)
    {
        var result = baseDate;
        int i = 0;
        while (i < formula.Length)
        {
            if (formula[i] == ' ') { i++; continue; }

            // Parse sign
            int sign = 1;
            if (i < formula.Length && (formula[i] == '+' || formula[i] == '-'))
            {
                if (formula[i] == '-') sign = -1;
                i++;
            }

            // Check for 'C' prefix (Current period)
            // In BC: CM/CW/CQ/CY = end of current period, -CM/-CW/-CQ/-CY = start of current period.
            if (i < formula.Length && (formula[i] == 'C' || formula[i] == 'c'))
            {
                i++;
                if (i < formula.Length)
                {
                    char unit = char.ToUpper(formula[i]);
                    i++;
                    // ISO day: Mon=0 … Sun=6
                    int isoDay = ((int)result.DayOfWeek + 6) % 7;
                    if (sign < 0)
                    {
                        // Beginning of period (ISO week starts on Monday)
                        result = unit switch
                        {
                            'D' => result,
                            'W' => result.AddDays(-isoDay),
                            'M' => new DateTime(result.Year, result.Month, 1),
                            'Q' => GetQuarterStart(result),
                            'Y' => new DateTime(result.Year, 1, 1),
                            _ => result
                        };
                    }
                    else
                    {
                        // End of period (ISO week ends on Sunday)
                        result = unit switch
                        {
                            'D' => result,
                            'W' => result.AddDays(6 - isoDay),
                            'M' => new DateTime(result.Year, result.Month,
                                        DateTime.DaysInMonth(result.Year, result.Month)),
                            'Q' => GetQuarterEnd(result),
                            'Y' => new DateTime(result.Year, 12, 31),
                            _ => result
                        };
                    }
                }
                continue;
            }

            // Parse number
            int number = 0;
            bool hasNumber = false;
            while (i < formula.Length && char.IsDigit(formula[i]))
            {
                number = number * 10 + (formula[i] - '0');
                hasNumber = true;
                i++;
            }

            if (!hasNumber) number = 1;

            // Parse unit
            if (i < formula.Length)
            {
                char unit = char.ToUpper(formula[i]);
                i++;
                int n = sign * number;
                result = unit switch
                {
                    'D' => result.AddDays(n),
                    'W' => result.AddDays(n * 7),
                    'M' => result.AddMonths(n),
                    'Q' => result.AddMonths(n * 3),
                    'Y' => result.AddYears(n),
                    _ => result
                };
            }
        }
        return result;
    }

    private static DateTime GetQuarterEnd(DateTime date)
    {
        int quarterMonth = ((date.Month - 1) / 3 + 1) * 3;
        return new DateTime(date.Year, quarterMonth,
            DateTime.DaysInMonth(date.Year, quarterMonth));
    }

    private static DateTime GetQuarterStart(DateTime date)
    {
        int quarterMonth = ((date.Month - 1) / 3) * 3 + 1;
        return new DateTime(date.Year, quarterMonth, 1);
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

    /// <summary>
    /// Convert a local-wall-clock DateTime to UTC, using the supplied timezone as the
    /// local reference. Applied on the write side of CreateDateTime / DaTi2Variant so
    /// that BC's read-side ConvertTimeFromUtc(value, TimeZoneInfo.Local) correctly
    /// inverts it, making DT2Time(CreateDateTime(D, T)) round-trip on any host. On
    /// UTC hosts (CI) this is a no-op, matching the baseline; on non-UTC hosts
    /// (typical Windows dev boxes) it removes the +offset that would otherwise
    /// appear in DT2Time.
    ///
    /// DST policy is deterministic so Windows and Linux produce identical
    /// NavDateTime.value ticks on the same input:
    /// - Ambiguous local times (fall-back hour): use the standard-time offset.
    /// - Invalid local times (spring-forward gap): shift forward to the first
    ///   valid local instant (end of the gap).
    /// </summary>
    internal static DateTime SafeLocalToUtc(DateTime wallClockLocal, TimeZoneInfo localTz)
    {
        // CI runs Linux/UTC — skip the DST probes entirely on the common path.
        if (ReferenceEquals(localTz, TimeZoneInfo.Utc))
            return DateTime.SpecifyKind(wallClockLocal, DateTimeKind.Utc);

        var unspec = DateTime.SpecifyKind(wallClockLocal, DateTimeKind.Unspecified);
        if (localTz.IsInvalidTime(unspec))
        {
            while (localTz.IsInvalidTime(unspec))
                unspec = unspec.AddMinutes(1);
            return TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(unspec, DateTimeKind.Unspecified), localTz);
        }
        if (localTz.IsAmbiguousTime(unspec))
        {
            var offsets = localTz.GetAmbiguousTimeOffsets(unspec);
            var standardOffset = PickStandardOffset(offsets, localTz.BaseUtcOffset);
            return DateTime.SpecifyKind(unspec - standardOffset, DateTimeKind.Utc);
        }
        return TimeZoneInfo.ConvertTimeToUtc(unspec, localTz);
    }

    /// <summary>
    /// Select the standard-time offset from the array returned by
    /// TimeZoneInfo.GetAmbiguousTimeOffsets. Per MS docs the array order is undefined,
    /// so the match must be done against BaseUtcOffset rather than a fixed index.
    /// Returns offsets[0] as a last-resort fallback if neither element matches
    /// (should not happen for a valid ambiguous-time input).
    /// </summary>
    internal static TimeSpan PickStandardOffset(TimeSpan[] offsets, TimeSpan baseUtcOffset)
    {
        for (int i = 0; i < offsets.Length; i++)
            if (offsets[i] == baseUtcOffset) return offsets[i];
        return offsets[0];
    }

    // Cache the backing field for NavDateTime construction via reflection.
    // NavDateTime.Create(DateTime) and operator+(Int64) both trigger loading of
    // Telemetry.Abstractions in BC 28+, which is unavailable outside the service tier.
    // Constructing via Activator + field set bypasses all such dependencies.
    private static readonly System.Reflection.FieldInfo? NavDateTimeValueField =
        GetNavBackingValueField(typeof(NavDateTime));

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

    // -----------------------------------------------------------------------
    // Replacement for ALSystemString.ALSelectStr
    // AL SelectStr(Number, String) returns the Nth comma-delimited token
    // (1-based). Throws if Number is out of range.
    // -----------------------------------------------------------------------

    /// <summary>
    /// AL <c>SelectStr(n, s)</c> — returns the Nth comma-separated token from s (1-based).
    /// Throws when n is less than 1 or greater than the number of tokens.
    /// </summary>
    public static string SelectStr(int n, string s)
    {
        var parts = (s ?? "").Split(',');
        if (n < 1 || n > parts.Length)
            throw new Exception($"The SELECTSTR comma-string {s} does not contain a value for index {n}.");
        return parts[n - 1];
    }

    // -----------------------------------------------------------------------
    // Replacement for ALSystemString.ALIncStr
    // AL IncStr(s) increments the last numeric sequence found in s.
    // e.g. 'DOC001' -> 'DOC002', 'A099' -> 'A100'.
    // -----------------------------------------------------------------------

    /// <summary>
    /// AL <c>IncStr(s)</c> — increments the trailing numeric sequence in s,
    /// preserving leading zeros and width. Returns s unchanged if no digits found.
    /// </summary>
    public static string IncStr(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        // Find the last run of digit characters
        int end = s.Length - 1;
        while (end >= 0 && !char.IsDigit(s[end]))
            end--;
        if (end < 0) return s; // no digits
        int start = end;
        while (start > 0 && char.IsDigit(s[start - 1]))
            start--;
        var digits = s.Substring(start, end - start + 1);
        var incremented = (long.Parse(digits) + 1).ToString().PadLeft(digits.Length, '0');
        return s.Substring(0, start) + incremented + s.Substring(end + 1);
    }

    // -----------------------------------------------------------------------
    // Replacement for ALSystemString.ALConvertStr
    // AL ConvertStr(String, FromChars, ToChars) replaces each character in
    // FromChars with the corresponding character in ToChars.
    // -----------------------------------------------------------------------

    /// <summary>
    /// AL <c>ConvertStr(s, fromChars, toChars)</c> — character-for-character
    /// replacement. Each character in fromChars is replaced with the corresponding
    /// character in toChars. FromChars and ToChars must have the same length.
    /// </summary>
    public static string ConvertStr(string s, string fromChars, string toChars)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (string.IsNullOrEmpty(fromChars)) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
        {
            int idx = fromChars.IndexOf(c);
            sb.Append(idx >= 0 && idx < toChars.Length ? toChars[idx] : c);
        }
        return sb.ToString();
    }

    // -----------------------------------------------------------------------
    // Replacement for ALSystemString.ALCopyStr (2-param variant)
    // AL CopyStr(String, Position) returns the substring from Position to end.
    // -----------------------------------------------------------------------

    /// <summary>
    /// AL <c>CopyStr(s, position)</c> — returns the substring starting at
    /// position (1-based) through the end of the string.
    /// </summary>
    public static string CopyStr(string s, int position)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (position < 1) position = 1;
        if (position > s.Length) return "";
        return s.Substring(position - 1);
    }

    // -----------------------------------------------------------------------
    // SecretStrSubstNo() replacement
    // Global AL function SecretStrSubstNo(format, args) → SecretText.
    // BC emits ALSystemString.ALSecretStrSubstNo(format, arg1, ...).
    // We reuse AlCompat.StrSubstNo for the string formatting and wrap in
    // NavSecretText.Create().
    // -----------------------------------------------------------------------

    /// <summary>
    /// AL <c>SecretStrSubstNo(format, args)</c> — substitutes %1..%N placeholders
    /// and returns the result as a <c>NavSecretText</c>.
    /// </summary>
    public static NavSecretText SecretStrSubstNo(string format, params Microsoft.Dynamics.Nav.Runtime.NavValue[] args)
    {
        var result = StrSubstNo(format, args);
        return NavSecretText.Create(result);
    }

    // -----------------------------------------------------------------------
    // Unwrap() replacement
    // AL SecretText.Unwrap() → Text.
    // BC emits x.ALUnwrap(). In BC 27.x, ALUnwrap() loads CodeAnalysis 16.4.x
    // at runtime, which is not available in the runner's DLL path.
    // We extract the string via reflection and wrap in a NavText.
    // -----------------------------------------------------------------------

    /// <summary>
    /// AL <c>SecretText.Unwrap()</c> — extracts the underlying string value from a
    /// <c>NavSecretText</c> struct without calling the native <c>ALUnwrap()</c>
    /// (which loads an unavailable CodeAnalysis assembly in BC 27.x+).
    /// </summary>
    public static NavText Unwrap(NavSecretText st)
    {
        if (st.ALIsEmpty()) return new NavText("");
        // NavSecretText is a struct; box it so reflection can read instance fields.
        object boxed = st;
        var flags = System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public;
        foreach (var field in typeof(NavSecretText).GetFields(flags))
        {
            if (field.FieldType == typeof(string))
            {
                var val = field.GetValue(boxed);
                if (val is string s)
                    return new NavText(s);
            }
        }
        foreach (var prop in typeof(NavSecretText).GetProperties(flags))
        {
            if (prop.PropertyType == typeof(string) && prop.CanRead)
            {
                try
                {
                    var val = prop.GetValue(boxed);
                    if (val is string s)
                        return new NavText(s);
                }
                catch { }
            }
        }
        // Last resort — ToString() returns the value in BC 26.x; may not in 27.x.
        return new NavText(st.ToString() ?? "");
    }

    // -----------------------------------------------------------------------
    // Evaluate(var Variable; Text): Boolean
    // BC lowers AL Evaluate() to NavFormatEvaluateHelper.Evaluate(session, ref var, text).
    // The rewriter strips the session arg, leaving AlCompat.Evaluate(ref var, text).
    // Type-specific overloads handle the common AL variable types. Returns true on
    // success (variable is set), false on failure (variable is unchanged).
    // -----------------------------------------------------------------------

    public static bool Evaluate(ref int result, NavText text)
        => Evaluate(ref result, text.ToString());

    public static bool Evaluate(ref int result, string text)
    {
        if (int.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
        { result = v; return true; }
        return false;
    }

    public static bool Evaluate(ref bool result, NavText text)
        => Evaluate(ref result, text.ToString());

    public static bool Evaluate(ref bool result, string text)
    {
        var t = text.Trim().ToLowerInvariant();
        if (t == "true" || t == "yes" || t == "1") { result = true; return true; }
        if (t == "false" || t == "no" || t == "0") { result = false; return true; }
        return false;
    }

    public static bool Evaluate(ref Decimal18 result, NavText text)
        => Evaluate(ref result, text.ToString());

    public static bool Evaluate(ref Decimal18 result, string text)
    {
        if (decimal.TryParse(text.Trim(), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
        { result = new Decimal18(v); return true; }
        return false;
    }

    public static bool Evaluate(ref NavText result, NavText text)
    { result = text; return true; }

    public static bool Evaluate(ref NavText result, string text)
    { result = new NavText(text); return true; }

    public static bool Evaluate(ref long result, NavText text)
        => Evaluate(ref result, text.ToString());

    public static bool Evaluate(ref long result, string text)
    {
        if (long.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
        { result = v; return true; }
        return false;
    }

    // XmlDocument node-manipulation stubs.
    // NavXmlDocument.ALRemove/ALAddAfterSelf/ALAddBeforeSelf/ALReplaceWith reach
    // into NavEnvironment (BC service-tier logging) which is unavailable standalone
    // and throws TypeInitializationException.  These helpers dispatch through the
    // NavXmlNode path (which works) for node-typed receivers, and are no-ops for
    // NavXmlDocument (standalone documents have no parent to manipulate).
    public static void XmlRemove(object node)
    {
        // NavXmlDeclaration: Remove() throws NavNCLInvalidOperationException in BC's runtime
        // (declarations are not regular child nodes). Treat as no-op.
        if (node is NavXmlDeclaration) return;
        // NavXmlDocument checked before NavXmlNode: on some BC versions NavXmlDocument
        // inherits NavXmlNode, and ALRemove on a document reaches NavEnvironment (service-tier
        // logging) which is unavailable standalone. A document has no parent, so no-op is correct.
        if (node is NavXmlDocument) return;
        if (node is NavXmlNode n) n.ALRemove(DataError.ThrowError);
    }

    public static void XmlAddAfterSelf(object node, NavXmlNode sibling)
    {
        if (node is NavXmlDeclaration) return;
        if (node is NavXmlDocument) return;
        if (node is NavXmlNode n) n.ALAddAfterSelf(DataError.ThrowError, sibling);
    }

    public static void XmlAddBeforeSelf(object node, NavXmlNode sibling)
    {
        if (node is NavXmlDeclaration) return;
        if (node is NavXmlDocument) return;
        if (node is NavXmlNode n) n.ALAddBeforeSelf(DataError.ThrowError, sibling);
    }

    public static void XmlReplaceWith(object node, NavXmlNode replacement)
    {
        if (node is NavXmlDeclaration) return;
        if (node is NavXmlDocument) return;
        if (node is NavXmlNode n) n.ALReplaceWith(DataError.ThrowError, replacement);
    }

    // BC transpiles the xpath argument to a plain string (not NavText), so both
    // overloads accept string to match what the rewriter emits.
    //
    // The DataError argument is forwarded verbatim from the BC-generated call.
    // BC uses DataError.ReturnFalse for SelectSingleNode — replacing it with
    // DataError.ThrowError turns a "return false on no-match" into a throw.
    //
    // ALSelectNodes/ALSelectSingleNode are NOT virtual on NavXmlNode; dynamic dispatch
    // ensures the concrete type's override is called (matching the pre-interceptor
    // behaviour where BC-generated code held the concrete-typed reference).
    //
    // NavXmlDeclaration: declarations have no child nodes. Return false and leave the
    // caller's NodeList variable at its default (null). The caller must not dereference
    // it after a failed SelectNodes call — matching real BC behaviour.
    public static bool XmlSelectNodes(object node, DataError de, string xpath, ByRef<NavXmlNodeList> nodeListRef)
    {
        if (node is NavXmlDeclaration)
            return false;
        dynamic dyn = node;
        return dyn.ALSelectNodes(de, xpath, nodeListRef);
    }

    // NavXmlDeclaration.ALSelectSingleNode also throws NavNCLNotSupportedOperationException.
    // Declarations have no child nodes — return false regardless of DataError.
    public static bool XmlSelectSingleNode(object node, DataError de, string xpath, ByRef<NavXmlNode> resultRef)
    {
        if (node is NavXmlDeclaration)
            return false;
        dynamic dyn = node;
        return dyn.ALSelectSingleNode(de, xpath, resultRef);
    }

    // -----------------------------------------------------------------------
    // ErrorInfo.Create(message) safe factory
    // NavALErrorInfo.ALCreate(msg, ...) calls UpdateWithRecordInfo() which loads
    // Microsoft.Dynamics.Nav.CodeAnalysis at runtime — unavailable in standalone mode.
    // This factory creates NavALErrorInfo directly and sets ALMessage without
    // going through the static factory method.
    // -----------------------------------------------------------------------
    public static NavALErrorInfo CreateErrorInfo(NavText message)
    {
        var ei = new NavALErrorInfo();
        ei.ALMessage = message.ToString();
        return ei;
    }

    /// <summary>
    /// String overload — BC may emit ErrorInfo.Create('literal') as a raw C# string
    /// argument.  Without this overload, Roslyn reports CS1503 (string → NavText).
    /// </summary>
    public static NavALErrorInfo CreateErrorInfo(string message)
    {
        var ei = new NavALErrorInfo();
        ei.ALMessage = message;
        return ei;
    }

    public static NavALErrorInfo CreateErrorInfo()
    {
        return new NavALErrorInfo();
    }

    /// <summary>
    /// After <c>RuntimeHelpers.GetUninitializedObject</c>, call
    /// <c>InitializeComponent</c> (and <c>InitializeGlobalVariables</c> if present)
    /// on the instance to run field initializers that the constructor would have run.
    /// Then, for any remaining reference-type backing fields that are still null,
    /// initialize them to safe defaults:
    /// <list type="bullet">
    ///   <item><description><see cref="MockRecordHandle"/> — <c>new MockRecordHandle(0)</c></description></item>
    ///   <item><description>Any other type with a public parameterless constructor — <c>Activator.CreateInstance</c></description></item>
    /// </list>
    /// This prevents <see cref="NullReferenceException"/> in event subscriber and
    /// record trigger bodies when the generated class has global variable backing
    /// fields that are not covered by the explicit Rec/xRec wiring in
    /// <see cref="MockRecordHandle.TryFireRecordTrigger"/> or the
    /// <c>InitializeComponent</c> call in <see cref="FireEvent"/>.
    /// </summary>
    /// <param name="instance">Object created via <c>GetUninitializedObject</c>.</param>
    public static void InitializeUninitializedObject(object instance)
    {
        var type = instance.GetType();

        // 1. Run InitializeComponent (sets fields with initializer expressions).
        var initMethod = type.GetMethod("InitializeComponent",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance);
        try { initMethod?.Invoke(instance, null); } catch { /* swallow — better a default than a crash */ }

        // 2. Run InitializeGlobalVariables if present (some generated classes use this wrapper).
        var initGlobals = type.GetMethod("InitializeGlobalVariables",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance);
        if (initGlobals != null && initGlobals != initMethod)
            try { initGlobals.Invoke(instance, null); } catch { /* swallow */ }

        // 3. Walk all instance fields; default-initialize any reference-type field still null.
        foreach (var field in type.GetFields(
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic))
        {
            if (field.FieldType.IsValueType) continue;
            if (field.GetValue(instance) != null) continue;

            // MockRecordHandle — safe default with table 0.
            if (field.FieldType == typeof(MockRecordHandle))
            {
                field.SetValue(instance, new MockRecordHandle(0));
                continue;
            }

            // Other reference types — try a parameterless constructor.
            try
            {
                var ctor = field.FieldType.GetConstructor(System.Type.EmptyTypes);
                if (ctor != null)
                    field.SetValue(instance, ctor.Invoke(null));
            }
            catch { /* swallow — better null than crash-on-construction */ }
        }
    }

}

/// <summary>
/// Extension methods for BC-emitted <c>.ALInvoke()</c> calls on types that lack
/// an instance <c>ALInvoke</c> method.
///
/// The BC compiler sometimes wraps expression results in <c>.ALInvoke()</c> to force
/// evaluation — for example when a TestPage field's <c>.Value()</c> (which returns
/// <c>string</c>) is used as a method argument:
/// <code>Assert.AreEqual(VendorName, VendorCard.Name.Value(), '...')</code>
/// BC lowers this to <c>...GetField(hash).ALValue.ALInvoke()</c>, where
/// <c>.ALValue</c> returns <c>object?</c> (concretely a string at runtime).
///
/// Types that define their own <c>ALInvoke()</c> (e.g. <see cref="MockTestPageField"/>,
/// <see cref="MockTestPageAction"/>) are unaffected — C# always prefers instance
/// methods over extension methods.
///
/// Without this extension, the Roslyn compilation stage emits CS1061 because
/// <c>string</c> (and other non-mock types) have no <c>ALInvoke</c> method.
/// Closes #1298.
/// </summary>
public static class ALInvokeExtensions
{
    /// <summary>
    /// Identity no-op: returns <paramref name="value"/> unchanged.
    /// Satisfies BC-generated <c>.ALInvoke()</c> calls on arbitrary receiver types.
    /// </summary>
    public static T ALInvoke<T>(this T value) => value;
}
