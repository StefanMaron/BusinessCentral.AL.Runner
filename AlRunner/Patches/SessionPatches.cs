// SessionPatches — replacements for NavSession property/method NREs.
//
// The skeleton NavSession is constructed with GetUninitializedObject so most of its
// internal state (globalLanguageStack, Database.SecurityAndLicense, cultureSettings,
// Diagnostics, …) is null. These replacements give safe defaults that let downstream
// code paths complete without NREs.
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AlRunner;

public static partial class BcRuntime
{
    // NOTE: still consumed by NclCecilRewrite.cs to Cecil-rewrite NavSystemCodeunit.get_Session
    // (a different type from NavApplicationObjectBase.get_Session — its own JmpHook.Apply call
    // site was deleted as an orphan, #1883 follow-up, but this shared helper is not dead).
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object? GetSessionReplacement(object self) => _skeletonSession;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object? GetCurrentMethodScopeReplacement(object self) => _skeletonRootScope;

    /// <summary>
    /// Replacement for TreeHandler.get_Session.
    /// The tree hierarchy is built from skeleton objects whose session fields are null.
    /// Always return the skeleton session so NavRecord.ctor and NavApplicationObjectBase.ctor
    /// can access a non-null session without needing a real BC tree.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Microsoft.Dynamics.Nav.Runtime.NavSession? TreeHandler_get_Session(object self)
        => _skeletonSession as Microsoft.Dynamics.Nav.Runtime.NavSession;

    /// <summary>
    /// Seed <c>NavSession.appId</c>, which AL's <c>Session.ApplicationIdentifier()</c> reads
    /// (via <c>ALSession.ALApplicationIdentifier</c> → <c>NavCurrentThread.Session.AppId</c>).
    /// The skeleton session comes from GetUninitializedObject, so the field is null and AL
    /// read back an empty string.
    ///
    /// The value is not invented. BC's own <c>AppId</c> setter resolves an unspecified
    /// application id to <c>ServerUserSettings.Instance.DefaultApplicationId.Value</c>,
    /// upper-cased — that setting's declared default is "NAV". The runner opens its session
    /// without a client-supplied application id, so "NAV" is precisely what real BC would
    /// have stored. Reading the setting rather than hardcoding keeps it correct if a future
    /// BC build changes the default.
    /// </summary>
    private static void SeedSkeletonAppId(Type sessType)
    {
        try
        {
            var fAppId = sessType.GetField("appId", BindingFlags.NonPublic | BindingFlags.Instance);
            if (fAppId == null)
            {
                Console.Error.WriteLine(
                    "[BcRuntime] WARN: NavSession.appId field not found — " +
                    "Session.ApplicationIdentifier() will read back empty.");
                return;
            }

            var typesAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types");
            var tSettings = typesAsm?.GetType("Microsoft.Dynamics.Nav.Types.ServerUserSettings");
            var instance = tSettings?.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var setting = instance == null ? null : tSettings!
                .GetProperty("DefaultApplicationId", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(instance);
            var value = setting?.GetType()
                .GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(setting) as string;

            if (string.IsNullOrEmpty(value))
            {
                Console.Error.WriteLine(
                    "[BcRuntime] WARN: ServerUserSettings.DefaultApplicationId is empty — " +
                    "Session.ApplicationIdentifier() will read back empty.");
                return;
            }

            AlRunner.Infrastructure.FieldPoke.SetInstance(fAppId, _skeletonSession!, value.ToUpperInvariant());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[BcRuntime] WARN: appId seed failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Replacement for <c>NavCurrentThread.get_Session</c>.
    ///
    /// BC's body is <c>NavThreadLocalStorage.Current.Session?.Target</c> — an
    /// <c>AsyncLocal</c>. The runner sets it once on the bootstrap thread
    /// (RecordPatches.WireNavCurrentThreadSession) and relies on ExecutionContext to carry
    /// it into test threads. That works for most of the runtime because most BC code reads
    /// <c>base.Session</c> off the tree instead — but any flow the context does not reach
    /// gets a silent null, and BC's callers do not null-check it. NavXmlPortExporter
    /// .ProcessTableElement opens with
    /// <c>NavCurrentThread.Session.ThrowSessionTerminatedExceptionIfStopping()</c> and NREs
    /// on its very first instruction.
    ///
    /// Falling back to the skeleton session is not a substitute for the real value: the
    /// runner is single-session by construction (docs/scope.md), so the skeleton session
    /// IS the session this thread would have been given had the context propagated. Same
    /// argument as TreeHandler.get_Session above and the NavSession.NCLMetadata → NavGlobal
    /// .NCLMetadata reroute — there is exactly one instance to hand back, so this cannot
    /// pick the wrong one.
    ///
    /// The AsyncLocal is still preferred when set, so anything that deliberately scopes a
    /// different session keeps working.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Microsoft.Dynamics.Nav.Runtime.NavSession? NavCurrentThread_get_Session()
    {
        var fromContext = ReadAsyncLocalSession();
        if (fromContext != null) return fromContext;
        return _skeletonSession as Microsoft.Dynamics.Nav.Runtime.NavSession;
    }

    private static PropertyInfo? _pTlsCurrent, _pTlsSession, _pRefTarget;
    private static bool _tlsResolved;

    /// <summary>Read NavThreadLocalStorage.Current.Session?.Target without recursing into the rewritten property.</summary>
    private static Microsoft.Dynamics.Nav.Runtime.NavSession? ReadAsyncLocalSession()
    {
        if (!_tlsResolved)
        {
            var nclAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
            var tTls = nclAsm?.GetType("Microsoft.Dynamics.Nav.Runtime.NavThreadLocalStorage");
            _pTlsCurrent = tTls?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
            _pTlsSession = tTls?.GetProperty("Session", BindingFlags.Public | BindingFlags.Instance);
            _tlsResolved = true;
        }
        try
        {
            var current = _pTlsCurrent?.GetValue(null);
            var reference = current == null ? null : _pTlsSession?.GetValue(current);
            if (reference == null) return null;
            _pRefTarget ??= reference.GetType().GetProperty("Target",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return _pRefTarget?.GetValue(reference) as Microsoft.Dynamics.Nav.Runtime.NavSession;
        }
        catch { return null; }
    }

    /// <summary>
    /// Build the ClientSettings (BC's "regional settings": ShortDatePattern, LongTimePattern,
    /// separators, …) that the skeleton session should carry.
    ///
    /// Why this is needed at all: the skeleton NavSession comes from
    /// RuntimeHelpers.GetUninitializedObject, so its <c>cultureSettings</c> field is
    /// <c>default(ClientSettings)</c> — every string property null. ClientSettings is a
    /// STRUCT, so BC's own guard in NavSessionOrDefaultProvider
    /// (<c>session?.RegionalSettings ?? DefaultRegionalSettings</c>) can never fire: the
    /// property is not null, it is merely empty. DateTimeParsingHelper.CreateExactDateTimePatterns
    /// then does <c>longTimePattern.Replace(...)</c> and NREs, which took out every AL
    /// <c>Evaluate()</c> into a Date/Time/DateTime.
    ///
    /// Faithful: this is BC's OWN construction path for a session-less default — see
    /// NavSessionOrDefaultProvider.AppInitFallbackValues, which does exactly
    /// <c>default(ClientSettings).Refresh(culture, culture)</c>. InvariantCulture is used as
    /// the culture so the patterns agree with the rest of the skeleton, which already reports
    /// InvariantCulture from <see cref="NavSession_get_Culture"/> and InvariantFormatSettings
    /// from <see cref="NavSession_SyncFormatSettings"/>.
    ///
    /// Returns the BOXED struct (ready for FieldInfo.SetValue), or null if the type/method
    /// cannot be resolved.
    /// </summary>
    public static object? BuildSkeletonRegionalSettings()
    {
        var clientSettingsType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("Microsoft.Dynamics.Nav.Types.ClientSettings"))
            .FirstOrDefault(t => t != null);
        if (clientSettingsType == null) return null;

        var refresh = clientSettingsType.GetMethod("Refresh",
            BindingFlags.Public | BindingFlags.Instance,
            null, new[] { typeof(CultureInfo), typeof(CultureInfo) }, null);
        if (refresh == null) return null;

        // Boxing the default struct once and invoking Refresh on the box mutates that box —
        // which is exactly what we then plant in the session's field.
        var boxed = Activator.CreateInstance(clientSettingsType);
        if (boxed == null) return null;
        refresh.Invoke(boxed, new object[] { RunnerSessionCulture, RunnerSessionCulture });
        return boxed;
    }

    /// <summary>
    /// The culture the skeleton session runs under.
    ///
    /// A real BC service tier never runs a session on InvariantCulture: the session's
    /// culture, its FormatSettings and the executing thread's culture all come from the
    /// user's language, and a default install is en-US. The runner used InvariantCulture
    /// throughout, which is observably different AL behaviour — e.g.
    /// <c>CurrReport.FormatRegion := 'en-US'</c> is a no-op on real BC (the session already
    /// formats as en-US, so ReportLocalLanguageScope.UpdateLanguage pushes nothing and the
    /// getter reads back the session's empty override stack), but on an invariant session
    /// BC correctly treats it as a genuine override and pushes it.
    ///
    /// Using en-US also makes a run independent of the host machine's locale, which
    /// otherwise leaks into <c>Thread.CurrentThread.CurrentCulture</c> comparisons inside
    /// BC's own code.
    /// </summary>
    public static readonly CultureInfo RunnerSessionCulture = CultureInfo.GetCultureInfo("en-US");

    /// <summary>
    /// Plant <see cref="BuildSkeletonRegionalSettings"/> into the skeleton session's
    /// <c>cultureSettings</c> field. Writes the field directly rather than going through the
    /// public setter, because <c>set_RegionalSettings</c> also calls CheckConnectionIsOpen()
    /// and the tenant/format-settings refresh chain, all of which NRE on the skeleton.
    /// The setter's own validation (every pattern non-null) is what this value satisfies.
    /// </summary>
    internal static void SeedSkeletonRegionalSettings(Type sessionType, object session)
    {
        var value = BuildSkeletonRegionalSettings();
        if (value == null)
        {
            Console.Error.WriteLine(
                "[BcRuntime] WARN: could not build skeleton ClientSettings — AL Evaluate() " +
                "into Date/Time/DateTime will NRE in DateTimeParsingHelper.");
            return;
        }
        var f = sessionType.GetField("cultureSettings", BindingFlags.NonPublic | BindingFlags.Instance);
        if (f == null)
        {
            Console.Error.WriteLine("[BcRuntime] WARN: NavSession.cultureSettings field not found.");
            return;
        }
        AlRunner.Infrastructure.FieldPoke.SetInstance(f, session, value);
    }

    private static object? _baseAppGroup;

    /// <summary>
    /// Replacement for NavSession.get_NavAppGroup. The real getter accesses
    /// <c>tenant.NavAppGroup</c>; on the skeleton session, <c>tenant</c> is null
    /// so the original NREs. NavForm..ctor reads this to resolve the page's
    /// owning app group. Return <c>NavAppGroup.BaseGroup</c> (the platform-base
    /// singleton already used by the metadata cache builders) so page/report
    /// ctors can complete.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object? NavSession_NavAppGroup(object? self)
    {
        if (_baseAppGroup != null) return _baseAppGroup;
        var nclAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        var tAppGroup = nclAsm?.GetType("Microsoft.Dynamics.Nav.Runtime.Apps.NavAppGroup");
        _baseAppGroup = tAppGroup?.GetProperty("BaseGroup",
                BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? tAppGroup?.GetField("BaseGroup",
                BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        return _baseAppGroup;
    }

    /// <summary>
    /// Replacement for NavSession.GetSecurityFilters — bypasses Database.SecurityAndLicense which
    /// NREs on the skeleton database. Return null; RecordImplementation treats null as "no security
    /// filters" (matches the IsPermissionSystemEnabled=false code path in the original method).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object? NavSession_GetSecurityFilters(object self,
        int companyNameToken, int tableId, object securityFilterType,
        object? callingObject, object? securableObject) => null;

    private static Microsoft.Dynamics.Nav.Runtime.FormatSettings? _cachedInvariantFmt;

    /// <summary>
    /// Replacement for NavSession.SyncFormatSettings().
    /// The real method reads <c>cultureSettings</c> (null in skeleton) → NRE.
    /// Returning <c>new FormatSettings()</c> here is unsafe — its default ctor
    /// allocates the <c>DateStdFormatStrings</c> / <c>TimeStdFormatStrings</c> /
    /// <c>DatetimeStdFormatStrings</c> arrays as <c>new string[10]</c> with all
    /// entries left null. <c>NavFormatEvaluateHelper.FormatWithFormatNumber</c>
    /// then calls <c>GetStandardFormat</c> which indexes <c>DatetimeStdFormatStrings[9]</c>,
    /// gets null, and passes it on to <c>FormatWithParsedFormatString</c> where
    /// <c>format.Length</c> NREs (decompile @ Microsoft.Dynamics.Nav.Ncl 196026-196140;
    /// NavDateTimeFormatter.GetStandardFormat @ 296313-296320).
    /// Fix per HANDOFF §2.4: return <c>NavSession.InvariantFormatSettings</c>, the
    /// same fully-populated singleton BC itself falls back to in
    /// <c>session?.FormatSettings ?? NavSession.InvariantFormatSettings</c>
    /// (see decompile line 195599). The static getter @ 206944-206957 builds it
    /// via <c>FormatSettings.Create(InvariantCulture.LCID, default(ClientSettings).Refresh(...))</c>
    /// which runs the full <c>DateUpdateFmt</c>/<c>TimeUpdateFmt</c>/<c>DatetimeUpdateFmt</c> path.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Microsoft.Dynamics.Nav.Runtime.FormatSettings NavSession_SyncFormatSettings(object? self)
    {
        if (_cachedInvariantFmt != null) return _cachedInvariantFmt;
        var nclAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");

        // Same construction BC uses for InvariantFormatSettings (decompile @ 206944-206957),
        // but for the runner's session culture rather than InvariantCulture — the skeleton
        // session IS an en-US session, so its format settings must say so.
        var fmtType = typeof(Microsoft.Dynamics.Nav.Runtime.FormatSettings);
        var create = fmtType.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static);
        var clientSettings = BuildSkeletonRegionalSettings();
        if (create != null && clientSettings != null
            && create.Invoke(null, new[] { (object)RunnerSessionCulture.LCID, clientSettings })
               is Microsoft.Dynamics.Nav.Runtime.FormatSettings built)
        {
            _cachedInvariantFmt = built;
            return built;
        }

        var navSessionType = nclAsm?.GetType("Microsoft.Dynamics.Nav.Runtime.NavSession");
        var prop = navSessionType?.GetProperty("InvariantFormatSettings",
            BindingFlags.NonPublic | BindingFlags.Static);
        var v = prop?.GetValue(null) as Microsoft.Dynamics.Nav.Runtime.FormatSettings;
        if (v != null) { _cachedInvariantFmt = v; return v; }
        // Fallback: empty FormatSettings (still NRE-prone for date/datetime/time
        // standard-format paths, but harmless for anything that doesn't index those arrays).
        return new Microsoft.Dynamics.Nav.Runtime.FormatSettings();
    }

    /// <summary>
    /// Replacement for NavSession.get_Culture / get_WindowsCulture.
    /// The real getters call CultureInfo.GetCultureInfo(int) with a culture id that
    /// is 0 on the skeleton session (uninitialized field) and throws
    /// ArgumentOutOfRangeException ("culture must be a non-negative and non-zero value").
    /// Return InvariantCulture so format/parse paths work in headless mode.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static CultureInfo NavSession_get_Culture(object? self) => RunnerSessionCulture;

    // Monotonic fake session counter (>= 1) handed out to ALSession.ALStartSession callers.
    // Faithful to the contract that StartSession assigns a fresh non-zero session id.
    private static int _alRunnerSessionCounter;

    /// <summary>
    /// In-scope (§3.9) replacement entry point for every ALSession.ALStartSession overload.
    /// The real implementation enqueues an async session via NavCurrentThread/Diagnostics
    /// which NRE on the skeleton runtime. Our model is "inline-synchronous execution":
    /// look up the codeunit by id, instantiate it under the skeleton tree-root, invoke
    /// OnRun once, then return true with a fresh non-zero session id. Failures under
    /// DataError.TrapError swallow the exception and return false (matching BC semantics
    /// where a trapped StartSession returns false without rethrowing).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    /// <summary>
    /// BC's refusal for <c>StartSession</c> called inside a test codeunit under any isolation mode
    /// other than <c>Disabled</c> (#2805), carried as <c>NavNCLDialogException</c> — the AL
    /// <c>Error()</c> carrier, so AL <c>asserterror</c> traps it and <c>GetLastErrorText</c>
    /// returns the message, which is what the corpus test does.
    ///
    /// <para>Deliberately NOT <c>RunnerOutOfScopeException</c>: that type announces a runner
    /// limitation and is what <c>tests/expectations/</c> classifies as an out-of-scope signal.
    /// This refusal is the opposite — real BC behaviour faithfully reproduced, which no
    /// expectation entry should ever mark out of scope. Same shape and reasoning as
    /// <c>MakeDataTransferException</c>. (Trappability is NOT the distinction, and an earlier
    /// version of this comment said it was: <c>RunnerOutOfScopeException</c> is a plain
    /// <c>System.Exception</c>, and <c>asserterror</c> catches it too — the runner's asserterror
    /// replacement is an unfiltered <c>catch (Exception)</c>. See
    /// <c>RunnerOutOfScopeException.cs</c>'s header and issue #2871.)</para>
    ///
    /// <para>The text is BC's own, measured byte-identical on all eight corpus legs (27.0, 27.3,
    /// 27.5, 28.0, 28.1, 28.2, 28.3, 28.4). <c>Assert.ExpectedError</c> is a substring match and
    /// the corpus pins the middle of this sentence, so the wording is not free to drift.</para>
    /// </summary>
    internal static System.Exception MakeStartSessionNotAllowedInTestException()
    {
        const string msg =
            "Sessions can only be started in tests that are run by a TestRunner that has "
            + "TestIsolation set to Disabled.";
        var t = System.Type.GetType(
            "Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLDialogException, Microsoft.Dynamics.Nav.Types");
        if (t != null)
        {
            var ctor = t.GetConstructor(new[] { typeof(string) });
            if (ctor != null) return (System.Exception)ctor.Invoke(new object[] { msg });
        }
        return new System.InvalidOperationException(msg);
    }

    /// <summary>
    /// Cecil patch target: <c>ALSession.ALStartSessionAsyncImpl</c> — the ONE seam every
    /// <c>ALStartSession</c> / <c>ALStartSessionAsync</c> overload in Ncl funnels into
    /// (each public overload's whole body is a forwarding call to it).
    ///
    /// <para><see cref="AlRunnerStartSession"/> above says it is "the replacement entry point
    /// for every ALSession.ALStartSession overload", and for SOURCE-COMPILED AL it is:
    /// BcAssembler's polyfill rewrites those call sites textually. PRECOMPILED AL —
    /// Base Application, System Application, any ISV DLL — calls Ncl directly, so it reached
    /// BC's real body instead, which opens a second NavSession and asks SQL for the database
    /// version:</para>
    ///
    /// <code>
    ///   ArgumentNullException: Value cannot be null. (Parameter 'database')
    ///      at NavSqlConnectionScope.Create(NavSession, NavDatabase, ...)
    ///      at NavSqlDatabaseProperties.ReadDatabaseVersionNo()
    ///      at NavSession.Open()
    ///      at ALSession.ALStartSessionAsyncImpl(...)
    ///      at Codeunit8705.UpdateFeatureUptakeStatus(..., performWriteTransactionsInASeparateSession, ...)
    /// </code>
    ///
    /// <para>That is BC's own feature-telemetry uptake logging, reached from an ordinary
    /// Base App install trigger — nothing the test author wrote. Found by letting install
    /// triggers run past their first await (#2960).</para>
    ///
    /// <para>Routing it to the same helper the source-compiled path already uses means one
    /// model of StartSession, not two: the #2805 TestIsolation guard, the session-id
    /// allocation and the TrapError semantics are all decided in exactly one place, so a
    /// precompiled caller and a source-compiled caller cannot observe different behaviour.
    /// The <c>invokeRunAsync</c> delegate BC threads through is not needed — the helper
    /// dispatches the target codeunit inline itself — and <c>session</c> is not either: the
    /// helper works from the skeleton session, which is the only session there is here.</para>
    ///
    /// <para>Returns an already-completed <see cref="System.Threading.Tasks.ValueTask{T}"/>,
    /// which is faithful for this runtime: the runner drives AL synchronously, and BC's own
    /// callers await the result before reading <c>sessionId</c>.</para>
    /// </summary>
    public static System.Threading.Tasks.ValueTask<bool> ALSession_ALStartSessionAsyncImpl(
        Microsoft.Dynamics.Nav.Runtime.NavSession session,
        Microsoft.Dynamics.Nav.Types.DataError errorLevel,
        Microsoft.Dynamics.Nav.Runtime.ByRef<int> sessionId,
        int objectId,
        string companyName,
        Microsoft.Dynamics.Nav.Runtime.NavRecord record,
        Microsoft.Dynamics.Nav.Runtime.NavDuration timeout,
        object invokeRunAsync)
        => new System.Threading.Tasks.ValueTask<bool>(
            AlRunnerStartSession(errorLevel, sessionId, objectId, companyName, record));

    public static bool AlRunnerStartSession(
        Microsoft.Dynamics.Nav.Types.DataError errorLevel,
        Microsoft.Dynamics.Nav.Runtime.ByRef<int> sessionId,
        int objectId,
        string? companyName,
        Microsoft.Dynamics.Nav.Runtime.NavRecord? record)
    {
        // #2805 — BC's TestIsolation guard, and it goes FIRST, outside the try, exactly where BC
        // puts it. Decompiled from ALSession.ALStartSessionAsyncImpl (bc281):
        //
        //     if (session.TestExecution != null
        //         && (!session.TestExecution.CommitTestCodeunits
        //             || !session.TestExecution.CommitTestFunctions))
        //     {
        //         throw new NavTestStartSessionNotAllowedException();
        //     }
        //
        // Three things about that placement are load-bearing, and all three are why this is not
        // simply folded into the try below:
        //
        //   * It precedes the session-id assignment. BC assigns sessionId.ObjectValue about forty
        //     lines later, after the new session is opened, so a refused call leaves the caller's
        //     by-ref untouched. The corpus pins that directly (SessionId = 0 after the refusal),
        //     and the runner's own "allocate the id up front so SessionId > 0 holds even if
        //     dispatch throws" ordering — right for a dispatch failure — is wrong for this one.
        //   * TrapError does NOT swallow it. BC's guard throws BEFORE its try block, so the catch
        //     that returns false for a trapped NavBaseException never sees this exception. Whether
        //     NavTestStartSessionNotAllowedException derives from NavBaseException is therefore
        //     irrelevant — settled by control flow, not by the type hierarchy, which is worth
        //     stating because #2805 asked for the derivation to be checked against the DLL and the
        //     answer is that it cannot matter. `trap` is deliberately not consulted here.
        //   * It is CONDITIONAL. Under TestIsolation = Disabled both commit flags are true and BC
        //     runs the session normally, so an unconditional refusal would be a different bug.
        //
        // The two conditions map to: InTestExecutionScope (BC's session.TestExecution != null,
        // read off BC's own executingTestCodeUnit field) and the run's isolation mode. Outside a
        // [Test] body — `execute` mode, an install trigger, a report — nothing is refused.
        if (InTestExecutionScope && TestExecutor.ActiveIsolation != TestIsolation.Disabled)
            throw MakeStartSessionNotAllowedInTestException();

        bool trap = errorLevel == Microsoft.Dynamics.Nav.Types.DataError.TrapError;
        try
        {
            var cuType = FindCodeunitTypePublic(objectId);
            if (cuType == null)
            {
                if (trap) return false;
                throw new InvalidOperationException(
                    $"ALStartSession: codeunit {objectId} is not present in the loaded test assembly.");
            }

            // Allocate a fresh session id BEFORE we dispatch so the contract
            // "SessionId > 0 after StartSession" holds even if dispatch throws
            // under TrapError (BC returns false in that case but does still
            // touch the by-ref).
            int newId = System.Threading.Interlocked.Increment(ref _alRunnerSessionCounter);
            try { sessionId.Value = newId; } catch { /* setter throws → ignore */ }

            // Construct under the skeleton tree root (same parent the existing
            // NavCodeunitHandle_CreateTarget uses) so NavApplicationObjectBase.ctor
            // can read TreeHandler.get_Session via our skeleton patches.
            var parent = (object?)_skeletonRootScope ?? RootTreeStub;
            var ctor = cuType.GetConstructors()
                .FirstOrDefault(c => c.GetParameters().Length == 1 &&
                    typeof(Microsoft.Dynamics.Nav.Runtime.ITreeObject)
                        .IsAssignableFrom(c.GetParameters()[0].ParameterType));
            if (ctor == null)
            {
                if (trap) return false;
                throw new InvalidOperationException(
                    $"ALStartSession: codeunit {objectId} has no single-arg ITreeObject constructor.");
            }
            var instance = ctor.Invoke(new object[] { parent! });

            // Resolve the worker's OWN trigger through the shared resolver — NOT by asking
            // for the sync name, which is what this used to do. `cuType` comes from
            // FindCodeunitTypePublic, which searches every loaded assembly including
            // precompiled dependencies, and BC's compiler emits those codeunits' OnRun as
            // `OnRunAsync`. `GetMethod("OnRun")` then returns the INHERITED, EMPTY
            // NavCodeunit.OnRun, which runs and does nothing — so StartSession on any Base
            // Application / System Application / ISV worker returned true having executed
            // none of its AL. Issue #2733; the identical shape in Codeunit.Run is what
            // ResolveOnRunTrigger was written for, and reports (#2734) and pages (#2729) had
            // it too.
            //
            // AwaitIfTask is not optional here: the async flavour returns a ValueTask, and
            // discarding it would park the worker's own Error() on the awaitable and drop it
            // — a silent failure of exactly the kind loud-failures.md forbids, and one that
            // would survive this fix while still looking green.
            var trigger = ResolveOnRunTrigger(cuType);
            if (trigger != null)
            {
                // The `record` arg is deliberately still null: BC's record-carrying
                // StartSession overloads hand the worker a row, and this replacement has never
                // passed it on. That is a separate defect (NavRecord does implement
                // INavRecordHandle, so it is a drop rather than a type limitation) and it needs
                // its own worker fixture that reads Rec — tracked separately, not widened into
                // this fix.
                var result = trigger.GetParameters().Length == 1
                    ? trigger.Invoke(instance, new object?[] { record })
                    : trigger.Invoke(instance, null);
                AwaitIfTask(result);
            }
            return true;
        }
        catch (TargetInvocationException tie) when (trap)
        {
            // TrapError semantics: swallow + return false.
            _ = tie;
            return false;
        }
        catch when (trap)
        {
            return false;
        }
    }
}
