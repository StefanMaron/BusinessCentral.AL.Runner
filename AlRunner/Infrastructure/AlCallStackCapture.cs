// AlCallStackCapture — captures the AL call stack at the moment an AL exception is thrown
// (before the CLR unwinds NavMethodScope frames) and formats it in the BC service-tier format:
//   "ObjectName"(ObjectType N).MethodName[(Trigger)] line L - AppName by Publisher version V
//
// Capture strategy: AppDomain.FirstChanceException fires BEFORE any catch/finally blocks
// run, so NavMethodScope frames are still live on the scope chain at that point.
// We filter to NavException subclasses (which carry AL semantic errors) and record
// operations exceptions; NullReferenceException etc. from the runner itself are left for
// the C# stack-trace fallback.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Infrastructure;

public static class AlCallStackCapture
{
    // NOTE: these are deliberately process-global, NOT [ThreadStatic]. BC's AL invoke
    // (and the test runner's per-test timeout watchdog, TestExecutor.InvokeWithTimeout)
    // execute the test body on a dedicated worker thread, while Clear() arms capture and
    // GetCaptured() reads it on the test-executor thread. A [ThreadStatic] flag set on the
    // executor thread is invisible to the worker thread that actually throws — so the FCE
    // handler would never capture. Tests run strictly sequentially (Thread.Join blocks per
    // test), and Thread.Start/Join provide the happens-before barriers, so a single global
    // pair is correct and race-free across the executor/worker thread boundary.

    /// <summary>The most recently captured AL call stack string (process-global).</summary>
    private static volatile string? _captured;

    /// <summary>
    /// AL stack captured per exception instance, at the exception's FIRST first-chance
    /// (the original throw point — the deepest, most complete chain). Keyed by instance so
    /// the test runner can ask for the stack of the *specific* exception that failed the
    /// test, rather than whatever NavException happened to be thrown last (a later rethrow
    /// during async unwinding, or an internally-caught asserterror exception, would
    /// otherwise clobber the single _captured slot with a shallower stack). ConditionalWeakTable
    /// keys on reference identity and lets dead exceptions be GC'd.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Exception, string> _byException = new();

    /// <summary>True while a test is executing; controls FCE capture (process-global).</summary>
    private static volatile bool _captureEnabled;

    /// <summary>Per-assembly app metadata (name, publisher, version).</summary>
    private static readonly Dictionary<Assembly, (string Name, string Publisher, string Version)>
        _assemblyInfo = new();
    private static readonly object _lock = new();

    // ─── Cached reflection handles ────────────────────────────────────────────

    private static bool _reflInit;
    private static object? _knownSession;               // NavSession, set by Initialize()
    private static FieldInfo?    _fSessCurrentScopeField; // NavSession.<CurrentMethodScope>k__BackingField
    private static PropertyInfo? _piParentScope;        // NavMethodScope.ParentScope
    private static PropertyInfo? _piIsRootScope;        // NavMethodScope.IsRootScope
    private static PropertyInfo? _piApplicationObject;  // NavMethodScope.ApplicationObject
    private static PropertyInfo? _piObjectName;         // NavApplicationObjectBase.ObjectName
    private static PropertyInfo? _piScopeName;          // NavMethodScope.ScopeName
    private static PropertyInfo? _piStatementNumber;    // NavMethodScope.StatementNumber
    private static FieldInfo?   _fiMsFlags;             // NavMethodScope.flags field
    private static Type?        _tMethodScopeFlags;     // MethodScopeFlags enum type
    private static Type?        _tNavException;         // NavException base type

    private static Type? _tSourceSpansAttr;
    private static PropertyInfo? _piEncodedSpans;       // SourceSpansAttribute.EncodedSpans
    private static Type? _tSignatureSpanAttr;
    private static PropertyInfo? _piSigEncodedSpan;     // SignatureSpanAttribute.EncodedSpan

    // ─── Public API ───────────────────────────────────────────────────────────

    /// <summary>The most recent capture (for AL-facing GetLastErrorCallStack and the timeout path).</summary>
    public static string? GetCaptured() => _captured;

    /// <summary>
    /// The AL stack captured for <paramref name="exception"/> at its original throw point.
    /// Falls back to the most-recent capture if this exact instance wasn't recorded
    /// (e.g. it was wrapped/re-created on the way out).
    /// <para>
    /// ONLY correct for a caller reporting a FAILING TEST. The fallback assumes
    /// <c>_captured</c> still holds that same test's own stack (possibly re-wrapped on the
    /// way out) — true within a single test's teardown, because <see cref="Clear"/> arms a
    /// fresh capture per test. It is NOT true for anything that runs outside a test (an
    /// install trigger, company initialisation, a bundle-level hook): in a resident
    /// <c>--watch</c> process <c>_captured</c> there can be leftover from an arbitrarily
    /// old, unrelated PREVIOUS cycle's test, and the fallback would silently attribute
    /// that stack to the wrong failure (#1958). Callers outside a test must use
    /// <see cref="GetCapturedFor"/> instead, which has no such fallback.
    /// </para>
    /// </summary>
    public static string? GetCaptured(Exception? exception)
    {
        if (exception != null && _byException.TryGetValue(exception, out var s))
            return s;
        return _captured;
    }

    /// <summary>
    /// The AL stack captured for <paramref name="exception"/> at its original throw point,
    /// or null. Unlike <see cref="GetCaptured(Exception?)"/> this has NO fallback to the
    /// most-recent capture — use this for anything that can fail outside a test (install
    /// triggers, company/tenant initialisation, bundle-level hooks), where the most-recent
    /// capture may belong to an unrelated earlier test, possibly from a previous
    /// <c>--watch</c> cycle (#1958). A null return means "no AL stack for this specific
    /// exception" — the caller should fall back to the real .NET stack, never to another
    /// exception's AL stack.
    /// </summary>
    public static string? GetCapturedFor(Exception? exception)
        => exception != null && _byException.TryGetValue(exception, out var s) ? s : null;

    public static string? CaptureCurrent()
    {
        try
        {
            var s = BuildStack();
            if (s != null) _captured = s;
            return s;
        }
        catch { return null; }
    }

    /// <summary>
    /// Call before each test to arm capture on this thread and clear any
    /// previously captured stack.
    /// </summary>
    public static void Clear()
    {
        _captured = null;
        _captureEnabled = true;
    }

    public static void RegisterAssemblyInfo(Assembly asm, string name, string publisher, string version)
    {
        lock (_lock) { _assemblyInfo[asm] = (name, publisher, version); }
    }

    /// <summary>
    /// One-time setup: stash the skeleton session and wire the FirstChanceException
    /// handler. Must be called after BC runtime patches are applied and the session
    /// exists, but before any tests run.
    /// </summary>
    public static void Initialize(object skeletonSession)
    {
        _knownSession = skeletonSession;
        EnsureReflInit(skeletonSession);
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
    }

    // ─── FirstChanceException handler ────────────────────────────────────────

    [HandleProcessCorruptedStateExceptions]
    private static void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs e)
    {
        // Only capture while a test is executing (armed by Clear()).
        if (!_captureEnabled) return;

        // Filter: only NavException subclasses carry AL-visible errors.
        if (_tNavException == null || !_tNavException.IsInstanceOfType(e.Exception)) return;

        // Capture only the FIRST first-chance for this exact exception instance — that is
        // the original throw point with the deepest, most complete scope chain. Later
        // first-chances for the same instance (ExceptionDispatchInfo.Throw rethrows during
        // async unwinding) fire at shallower scopes and would otherwise truncate the stack.
        if (_byException.TryGetValue(e.Exception, out _)) return;

        // Guard against re-entrant FCE (can happen if reflection below throws).
        _captureEnabled = false;
        try
        {
            var s = BuildStack();
            if (s != null)
            {
                _byException.AddOrUpdate(e.Exception, s);
                _captured = s;
            }
        }
        catch
        {
            // Swallow: FCE handler must never throw.
        }
        finally
        {
            _captureEnabled = true;
        }
    }

    /// <summary>Builds the AL call-stack string for the current scope chain, or null if none.</summary>
    private static string? BuildStack()
    {
        if (_knownSession == null) return null;

        // CurrentMethodScope is JMP-hooked to return the skeleton root scope.
        // We bypass the hook and read the backing field directly to get the
        // real innermost scope that was active when the exception was thrown.
        NavMethodScope? currentScope = null;
        if (_fSessCurrentScopeField != null)
        {
            var raw = _fSessCurrentScopeField.GetValue(_knownSession);
            currentScope = raw as NavMethodScope;
        }
        if (currentScope == null) return null;

        // Walk the ParentScope chain directly rather than NavMethodScope.StackTrace.
        // StackTrace yields only scopes with IsStackFrame=true; in the runner the
        // generic ALMethodScope frames produced for precompiled MS/ISV library calls
        // (Base App, Tests-TestLibraries, etc.) have IsStackFrame=false, so StackTrace
        // drops them and the captured AL stack stops at the first frame in the test's
        // own app. Walking ParentScope and formatting every frame that has an
        // ApplicationObject restores the full cross-app call stack; FormatFrame returns
        // null for the root/try/system scopes (null ApplicationObject), so they are
        // naturally excluded. A visited-set + depth cap guard against cycles.
        var sb = new StringBuilder();
        bool first = true;
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        object? cur = currentScope;
        int depth = 0;
        while (cur is NavMethodScope scope && depth++ < 500 && visited.Add(cur))
        {
            var line = FormatFrame(scope);
            if (line != null)
            {
                if (!first) sb.AppendLine();
                sb.Append(line);
                first = false;
            }
            if (_piIsRootScope?.GetValue(scope) is true) break;
            cur = _piParentScope?.GetValue(scope);
        }
        return first ? null : sb.ToString();
    }

    // ─── Private helpers ──────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? FormatFrame(NavMethodScope scope)
    {
        try
        {
            // Application object (null for root / try scopes)
            var appObj = _piApplicationObject?.GetValue(scope);
            if (appObj == null) return null;

            var objName    = _piObjectName?.GetValue(appObj) as string;
            var methodName = _piScopeName?.GetValue(scope) as string ?? "?";
            var stmtNo     = (int)(_piStatementNumber?.GetValue(scope) ?? 0);

            // Extract object type and ID from the declaring class name (e.g. "Codeunit60021").
            // EffectiveObjectId.ObjectNumber is not reliably populated in the runner (the ctor
            // replacement cannot safely copy a value-type struct via reflection), so we parse
            // the IL class name which is always present and correct.
            (string objType, int objNumber) = ParseObjectTypeAndId(appObj.GetType());
            if (objNumber == 0) return null;
            if (objName == null) objName = objNumber.ToString();

            bool isTrigger = GetIsTrigger(scope);
            int lineNo = GetRelativeLine(scope.GetType(), stmtNo);

            var (appName, publisher, version) = GetAppMeta(scope.GetType().Assembly);

            // Format:  "ObjectName"(ObjectType N).MethodName[(Trigger)] line L - App by Pub version V
            var sb = new StringBuilder();
            AppendQuoted(sb, objName);
            sb.Append('(').Append(objType).Append(' ').Append(objNumber).Append(").");
            sb.Append(methodName);
            if (isTrigger) sb.Append("(Trigger)");
            if (lineNo >= 0)
                sb.Append(" line ").Append(lineNo);
            if (appName != null)
                sb.Append(" - ").Append(appName).Append(" by ").Append(publisher).Append(" version ").Append(version);

            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parse the AL object type label and numeric ID from the runtime class name.
    /// BC emits class names like <c>Codeunit60021</c>, <c>Table18</c>, <c>Page1</c>.
    /// The scope class is nested inside the object class, so we walk up via
    /// <see cref="Type.DeclaringType"/> when needed.
    /// Returns ("CodeUnit"|"Page"|…, number) or ("?", 0) if unknown.
    /// </summary>
    /// <summary>Internal (not private): also used by AlCoverageTracker to resolve a
    /// scope's declaring AL object identity for the cobertura file mapping.</summary>
    internal static (string, int) ParseObjectTypeAndId(Type type)
    {
        // Walk up to the outermost non-nested type (scope classes are nested).
        var t = type;
        while (t.DeclaringType != null) t = t.DeclaringType;

        var name = t.Name;
        // Mapping from IL class-name prefix → BC call-stack label.
        // We try longest prefixes first so "Table" doesn't match "TableExtension".
        (string prefix, string label)[] prefixMap =
        [
            ("NavCodeunit",   "CodeUnit"),   // generic codeunit base class
            ("NavTestCodeunit","CodeUnit"),
            ("Codeunit",      "CodeUnit"),
            ("Table",         "Table"),
            // Table TRIGGER scopes (OnInsert/OnModify/…) are nested inside the table's
            // generated record wrapper class, named Record<N> — NOT Table<N> (confirmed
            // via DUMP_CS=1 on AlRunner.Tests/Fixtures/RecordTriggerXRec: `public sealed
            // class Record60100 : NavRecord { ... class OnInsert_Scope : ... }`). Without
            // this, every table trigger's scope resolved to ("?", 0) — id==0 — so
            // AlCoverageTracker silently dropped ALL table-trigger coverage, and any AL
            // stack trace originating inside a table trigger printed no object identity.
            ("Record",        "Table"),
            ("Page",          "Page"),
            ("Report",        "Report"),
            ("Query",         "Query"),
            ("XmlPort",       "XmlPort"),
            ("Enum",          "Enum"),
        ];

        foreach (var (prefix, label) in prefixMap)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                var rest = name.Substring(prefix.Length);
                // rest should be the numeric ID, possibly followed by "_v<n>" from versioned emit.
                var numPart = new string(rest.TakeWhile(char.IsDigit).ToArray());
                if (numPart.Length > 0 && int.TryParse(numPart, out int id) && id > 0)
                    return (label, id);
            }
        }
        return ("?", 0);
    }

    private static void AppendQuoted(StringBuilder sb, string name)
    {
        // BC always quotes object names in call-stack output.
        sb.Append('"');
        sb.Append(name.Replace("\"", "\"\""));
        sb.Append('"');
    }

    private static bool GetIsTrigger(NavMethodScope scope)
    {
        if (_tMethodScopeFlags == null || _fiMsFlags == null) return false;
        try
        {
            var flags = _fiMsFlags.GetValue(scope);
            if (flags == null) return false;
            // IsTrigger = 0x40 in MethodScopeFlags
            var isTriggerField = _tMethodScopeFlags.GetField("IsTrigger");
            if (isTriggerField == null) return false;
            var trigVal = (int)(isTriggerField.GetRawConstantValue() ?? 0);
            return (Convert.ToInt32(flags) & trigVal) != 0;
        }
        catch { return false; }
    }

    private static int GetRelativeLine(Type scopeType, int statementNumber)
    {
        if (_tSourceSpansAttr == null || _tSignatureSpanAttr == null) return -1;
        try
        {
            var srcAttr  = scopeType.GetCustomAttribute(_tSourceSpansAttr);
            var sigAttr  = scopeType.GetCustomAttribute(_tSignatureSpanAttr);
            if (srcAttr == null || sigAttr == null) return -1;

            var encodedSpans = _piEncodedSpans?.GetValue(srcAttr) as long[];
            if (encodedSpans == null || encodedSpans.Length == 0) return -1;

            // Clamp: IsAtExitStatement uses last span; statementNumber is 1-based
            var idx = statementNumber == int.MaxValue
                ? encodedSpans.Length - 1
                : Math.Min(statementNumber, encodedSpans.Length - 1);
            if (idx < 0) return -1;

            var encodedSpan    = encodedSpans[idx];
            var encodedSigSpan = (long)(_piSigEncodedSpan?.GetValue(sigAttr) ?? 0L);

            // Bit layout is shared with AlCoverageTracker — see AlSourceSpanCodec.
            return AlSourceSpanCodec.RelativeLine(encodedSpan, encodedSigSpan);
        }
        catch { return -1; }
    }

    private static (string? Name, string? Publisher, string? Version) GetAppMeta(Assembly asm)
    {
        lock (_lock)
        {
            if (_assemblyInfo.TryGetValue(asm, out var info))
                return (info.Name, info.Publisher, info.Version);
        }
        return (null, null, null);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EnsureReflInit(object session)
    {
        if (_reflInit) return;
        _reflInit = true;

        var sessionType = session.GetType();
        // The CurrentMethodScope property getter is JMP-hooked to return _skeletonRootScope.
        // Read the backing field directly to bypass the hook and get the real current scope.
        _fSessCurrentScopeField = sessionType.GetField("<CurrentMethodScope>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance);

        var scopeType = typeof(NavMethodScope);
        _piParentScope = scopeType.GetProperty("ParentScope",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        _piIsRootScope = scopeType.GetProperty("IsRootScope",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        _piApplicationObject = scopeType.GetProperty("ApplicationObject",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        _piScopeName = scopeType.GetProperty("ScopeName",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        _piStatementNumber = scopeType.GetProperty("StatementNumber",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        _fiMsFlags = scopeType.GetField("flags",
            BindingFlags.NonPublic | BindingFlags.Instance);
        _tMethodScopeFlags = _fiMsFlags?.FieldType;

        // NavApplicationObjectBase
        var appObjType = typeof(Microsoft.Dynamics.Nav.Runtime.NavApplicationObjectBase);
        _piObjectName = appObjType.GetProperty("ObjectName",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        // SourceSpansAttribute / SignatureSpanAttribute live in Microsoft.Dynamics.Nav.Ncl.dll
        var nclAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        if (nclAsm != null)
        {
            _tSourceSpansAttr = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.SourceSpansAttribute");
            _piEncodedSpans   = _tSourceSpansAttr?.GetProperty("EncodedSpans",
                BindingFlags.Public | BindingFlags.Instance);

            _tSignatureSpanAttr = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.SignatureSpanAttribute");
            _piSigEncodedSpan   = _tSignatureSpanAttr?.GetProperty("EncodedSpan",
                BindingFlags.Public | BindingFlags.Instance);
        }

        // NavException is in Microsoft.Dynamics.Nav.Types (not NCL).
        // It is the common base of NavNCLDialogException, NavCSideDuplicateKeyException, etc.
        var typesAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types");
        _tNavException = typesAsm?.GetType("Microsoft.Dynamics.Nav.Types.NavException");
    }
}
