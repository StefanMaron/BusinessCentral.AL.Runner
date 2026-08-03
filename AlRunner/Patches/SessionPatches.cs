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
    /// Replacement for NavIntegerFormatter.FormatWithFormatNumber.
    /// Real body calls value.ToInt32().ToString("d", session.WindowsCulture); on the
    /// skeleton runtime the NavValue passed in can be null (NavValue[] entries
    /// uninitialized in the AL emit's varargs-build), which NREs. Bypass: format
    /// any non-null int value with InvariantCulture; null becomes empty string.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string NavIntegerFormatter_FormatWithFormatNumber(
        object self,
        object? session,
        object? value,
        int length,
        int formatNumber,
        object formatsetting)
    {
        if (value == null) return string.Empty;
        try
        {
            // NavValue.ToInt32() — call via reflection to avoid hard reference.
            var toInt32 = value.GetType().GetMethod("ToInt32",
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (toInt32 != null)
            {
                var i = (int)toInt32.Invoke(value, null)!;
                return i.ToString("d", CultureInfo.InvariantCulture);
            }
        }
        catch { }
        return value.ToString() ?? string.Empty;
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
    public static bool AlRunnerStartSession(
        Microsoft.Dynamics.Nav.Types.DataError errorLevel,
        Microsoft.Dynamics.Nav.Runtime.ByRef<int> sessionId,
        int objectId,
        string? companyName,
        Microsoft.Dynamics.Nav.Runtime.NavRecord? record)
    {
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

            // Prefer the OnRun(INavRecordHandle) overload if present (record-arg
            // StartSession overloads). Fall back to OnRun() for parameterless workers.
            // The `record` arg is a NavRecord (the runtime type); the OnRun parameter
            // is INavRecordHandle. Pass null when the AL-emitted overload didn't
            // provide a record (preserves the worker's "no input" contract).
            var onRunRec = cuType.GetMethod("OnRun",
                BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(Microsoft.Dynamics.Nav.Runtime.INavRecordHandle) }, null);
            if (onRunRec != null)
            {
                onRunRec.Invoke(instance, new object?[] { null });
            }
            else
            {
                var onRun0 = cuType.GetMethod("OnRun",
                    BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                onRun0?.Invoke(instance, null);
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
