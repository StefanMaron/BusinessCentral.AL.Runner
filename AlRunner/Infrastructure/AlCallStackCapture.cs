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

            // Both are three-way (#4345): null means the answer could not be measured, and is
            // rendered as a runner-side marker rather than silently as BC's negative answer.
            bool? isTrigger = GetIsTrigger(scope);
            int? lineNo = GetRelativeLine(scope.GetType(), stmtNo);

            var (appName, publisher, version) = GetAppMeta(scope.GetType().Assembly);

            // Format:  "ObjectName"(ObjectType N).MethodName[(Trigger)] line L - App by Pub version V
            var sb = new StringBuilder();
            AppendQuoted(sb, objName);
            sb.Append('(').Append(objType).Append(' ').Append(objNumber).Append(").");
            sb.Append(methodName);
            if (isTrigger == true) sb.Append("(Trigger)");
            else if (isTrigger == null) sb.Append(UnknownTriggerMarker);
            if (lineNo == null) sb.Append(UnknownLineMarker);
            else if (lineNo >= 0) sb.Append(" line ").Append(lineNo.Value);
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
    /// <remarks>Internal (not private): also used by AlCoverageTracker to resolve a
    /// scope's declaring AL object identity for the cobertura file mapping.</remarks>
    internal static (string, int) ParseObjectTypeAndId(Type type)
    {
        // Walk up to the outermost non-nested type (scope classes are nested).
        var t = type;
        while (t.DeclaringType != null) t = t.DeclaringType;
        return ParseObjectTypeAndIdForTests(t.Name);
    }

    /// <summary>
    /// The name half of <see cref="ParseObjectTypeAndId(Type)"/>, split out so the prefix map
    /// can be asserted against the emitted names measured from BC rather than only through a
    /// live type (#3841 review). The caller passes an OUTERMOST type name.
    /// </summary>
    internal static (string, int) ParseObjectTypeAndIdForTests(string name)
    {
        // Mapping from IL class-name prefix → BC call-stack label.
        // We try longest prefixes first so "Table" doesn't match "TableExtension".
        (string prefix, string label)[] prefixMap =
        [
            ("NavCodeunit",   "CodeUnit"),   // generic codeunit base class
            ("NavTestCodeunit","CodeUnit"),
            ("Codeunit",      "CodeUnit"),
            // EXTENSION objects (#3833). Without these every extension scope parsed to
            // ("?", 0) and was dropped before any map was consulted, so an extension's
            // procedures and triggers were invisible to coverage, the statement tables and
            // DAP. Measured on BC 28.1.49838.54169: a tableextension's procedure emits
            // `TableExtension63701+Doubled_Scope_750224019`, a pageextension's
            // `PageExtension63721+Tripled_Scope_1853489953`, a reportextension's
            // `ReportExtension63741+Quadrupled_Scope_2053452689`. The id is the EXTENSION's own,
            // not the base object's. What keeps entries apart is the (label, id) PAIR, not the
            // id alone: a tableextension and a pageextension may legally share a number, and an
            // extension id may equal an unrelated base object's, so it is the distinct label
            // that separates them. Labels match the spelling RecordPatches.AlSourceParser
            // already uses. Two objects of the SAME kind and id in one map still overwrite —
            // the AL compiler rejects that within an app, and across apps the map has no
            // AppId dimension, which is older than this change and not altered by it.
            //
            // Placed before the base kinds to read in the order the note below describes,
            // but the ORDER IS NOT WHAT MAKES IT WORK, and it is worth saying so because the
            // note invites the opposite conclusion: the loop returns on the first successful
            // PARSE, not the first prefix match, and "Table" against `TableExtension63701`
            // leaves `Extension63701`, whose leading digit run is empty. Measured by moving
            // these three below `Enum` — the fact still passes.
            ("TableExtension",  "TableExtension"),
            ("PageExtension",   "PageExtension"),
            ("ReportExtension", "ReportExtension"),
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

    /// <summary>
    /// Whether this frame is an AL trigger: <c>true</c>/<c>false</c> only from a read that
    /// SUCCEEDED, and <c>null</c> when the answer could not be measured at all.
    /// <para>
    /// #4345: every unmeasurable exit here used to answer <c>false</c>, which is not a neutral
    /// sentinel — it is the load-bearing claim "this frame is not a trigger", rendered into
    /// AL-visible call-stack text. It does not throw, for three reasons specific to this call
    /// path: the only caller is <see cref="FormatFrame"/>, whose <c>catch</c> would turn a
    /// throw into a DROPPED frame; <see cref="BuildStack"/> appends a frame only when it
    /// rendered, with no marker and no counter, so a stack would read complete while missing a
    /// frame from the middle; and the cause is process-global cached state, so the loss would
    /// be the entire AL stack rather than one frame.
    /// </para>
    /// </summary>
    private static bool? GetIsTrigger(NavMethodScope scope)
    {
        if (_tMethodScopeFlags == null || _fiMsFlags == null)
        {
            WarnUnknownTriggerOnce(_fiMsFlags == null
                ? "NavMethodScope.flags field handle"
                : "MethodScopeFlags enum type handle");
            return null;
        }
        try
        {
            var flags = _fiMsFlags.GetValue(scope);
            if (flags == null)
            {
                WarnUnknownTriggerOnce("NavMethodScope.flags read back null");
                return null;
            }
            var isTriggerField = _tMethodScopeFlags.GetField("IsTrigger");
            if (isTriggerField == null)
            {
                WarnUnknownTriggerOnce("MethodScopeFlags declares no IsTrigger member");
                return null;
            }
            var trigVal = (int)(isTriggerField.GetRawConstantValue() ?? 0);
            return (Convert.ToInt32(flags) & trigVal) != 0;
        }
        catch (Exception ex)
        {
            WarnUnknownTriggerOnce($"reading MethodScopeFlags.IsTrigger threw {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// The frame's line number, <c>-1</c> when the frame genuinely HAS none, and <c>null</c>
    /// when the answer could not be measured.
    /// <para>
    /// #4345: unlike <see cref="GetIsTrigger"/>, this method's exits do not all mean the same
    /// thing, and conflating them would trade one defect for its mirror image
    /// (<c>.claude/rules/guards-need-a-third-state.md</c>: "a genuinely absent thing must stay
    /// a pass"). A scope type carrying no <c>SourceSpansAttribute</c>, or an empty span array,
    /// really has no line number — that stays <c>-1</c>, rendering no line, exactly as before.
    /// Only an unresolved cached attribute handle, or a throw, is unmeasurable.
    /// </para>
    /// </summary>
    private static int? GetRelativeLine(Type scopeType, int statementNumber)
    {
        // Unmeasurable: EnsureReflInit never bound these, so nothing can be read for ANY frame.
        if (_tSourceSpansAttr == null || _tSignatureSpanAttr == null)
        {
            WarnUnknownLineOnce(_tSourceSpansAttr == null
                ? "SourceSpansAttribute type handle"
                : "SignatureSpanAttribute type handle");
            return null;
        }
        try
        {
            var srcAttr  = scopeType.GetCustomAttribute(_tSourceSpansAttr);
            var sigAttr  = scopeType.GetCustomAttribute(_tSignatureSpanAttr);
            // Genuinely absent: this scope type carries no span attribute, so it HAS no line.
            if (srcAttr == null || sigAttr == null) return -1;

            var encodedSpans = _piEncodedSpans?.GetValue(srcAttr) as long[];
            // Genuinely absent: an attribute present but declaring no spans.
            if (encodedSpans == null || encodedSpans.Length == 0) return -1;

            // Clamp: IsAtExitStatement uses last span; statementNumber is 1-based.
            var idx = statementNumber == int.MaxValue
                ? encodedSpans.Length - 1
                : Math.Min(statementNumber, encodedSpans.Length - 1);
            // Genuinely absent, and unreachable as written: the Length == 0 guard above already
            // returned, so Length >= 1 makes Length - 1 >= 0, and Math.Min of that with a
            // statementNumber cannot go below 0 for a non-negative statementNumber. Kept as a
            // floor against a negative statementNumber, which would be a frame with no locatable
            // statement rather than a failed read.
            if (idx < 0) return -1;

            var encodedSpan    = encodedSpans[idx];
            var encodedSigSpan = (long)(_piSigEncodedSpan?.GetValue(sigAttr) ?? 0L);

            // Bit layout is shared with AlCoverageTracker — see AlSourceSpanCodec.
            return AlSourceSpanCodec.RelativeLine(encodedSpan, encodedSigSpan);
        }
        catch (Exception ex)
        {
            // Unmeasurable: the read started and failed. Distinct from the -1 rows above.
            WarnUnknownLineOnce($"reading the source spans threw {ex.GetType().Name}");
            return null;
        }
    }

    // ── Third-state markers and their one-shot diagnostics (#4345) ───────────────
    //
    // Both markers are deliberately unmistakable for BC output. BC emits `MethodName(Trigger)`
    // and ` line 12`; a spelling such as `(Trigger?)` would read as BC's own answer with a
    // question mark, which is precisely the confusion these exist to prevent. They are
    // constants rather than call-site literals so a test can assert on them without
    // duplicating the string.

    /// <summary>Rendered in place of <c>(Trigger)</c> when trigger-ness could not be read.</summary>
    internal const string UnknownTriggerMarker = "(al-runner:trigger-unknown)";

    /// <summary>Rendered in place of <c> line N</c> when the line number could not be read.</summary>
    /// <remarks>Deliberately does NOT start with <c>" line "</c>: BC emits <c> line 12</c>, and a
    /// marker beginning that way would read as BC output whose number went missing.</remarks>
    internal const string UnknownLineMarker = " (al-runner:line-unknown)";

    // Latched per process, not per frame: every unmeasurable exit above reads an
    // EnsureReflInit-cached static, so whatever nulls one nulls it for EVERY frame of every
    // stack. A per-frame write would print once per frame per exception.
    private static bool _warnedUnknownTrigger;
    private static bool _warnedUnknownLine;

    private static void WarnUnknownTriggerOnce(string what)
    {
        if (_warnedUnknownTrigger) return;
        _warnedUnknownTrigger = true;
        WriteWarning($"AL call stack: cannot tell whether frames are triggers ({what} " +
                     $"unresolved); affected frames are marked '{UnknownTriggerMarker}'.");
    }

    private static void WarnUnknownLineOnce(string what)
    {
        if (_warnedUnknownLine) return;
        _warnedUnknownLine = true;
        WriteWarning($"AL call stack: cannot read frame line numbers ({what} unresolved); " +
                     $"affected frames are marked '{UnknownLineMarker.TrimStart()}'.");
    }

    // FormatFrame runs under the FirstChanceException handler, whose own catch carries
    // "FCE handler must never throw" — so the diagnostic must not become the thing that
    // throws while reporting an exception (a closed Console.Error during shutdown, say).
    private static void WriteWarning(string message)
    {
        try { Console.Error.WriteLine($"warning: {message}"); }
        catch { /* a diagnostic is never worth failing the capture it describes */ }
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
