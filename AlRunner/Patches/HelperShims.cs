// BcRuntime.Helpers — generic NoOp / ReturnX shims that JMP-hooks redirect to.
//
// Each helper matches the receiver+args slot count of the call sites it replaces:
//   * For an instance method, the static replacement takes one extra leading object
//     parameter for the receiver (`this`). e.g. an instance void method with two
//     reference args needs a NoOp3.
//   * For value-returning helpers (Return*), the return type's CLR slot must match
//     the original — bool→bool, ValueTask→ValueTask, etc.
//
// All helpers are `[MethodImpl(NoInlining)]` because the JIT must produce a real
// callable function pointer for JmpHook to patch.
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AlRunner;

public static partial class BcRuntime
{
    private static FieldInfo? _fTruncateTtdpTable;

    [MethodImpl(MethodImplOptions.NoInlining)] public static void NoOp_0Args() { }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void NoOp_OneArg(object? a) { }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void NoOp2(object? a, object? b) { }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void NoOp3(object? a, object? b, object? c) { }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void NoOp4(object? a, object? b, object? c, object? d) { }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void NoOp5(object? a, object? b, object? c, object? d, object? e) { }

    [MethodImpl(MethodImplOptions.NoInlining)] public static bool ReturnFalse_0Args() => false;
    [MethodImpl(MethodImplOptions.NoInlining)] public static bool ReturnFalse_1Arg(object? a) => false;
    [MethodImpl(MethodImplOptions.NoInlining)] public static bool ReturnFalse_2Args(object? a, object? b) => false;
    [MethodImpl(MethodImplOptions.NoInlining)] public static bool ReturnFalse_4Args(object? a, object? b, object? c, object? d) => false;
    [MethodImpl(MethodImplOptions.NoInlining)] public static bool ReturnFalse_7Args(
        object? a, object? b, object? c, object? d, object? e, object? f, object? g) => false;
    [MethodImpl(MethodImplOptions.NoInlining)] public static bool ReturnFalse_8Args(
        object? a, object? b, object? c, object? d, object? e, object? f, object? g, object? h) => false;
    [MethodImpl(MethodImplOptions.NoInlining)] public static bool ReturnFalse_10Args(
        object? a, object? b, object? c, object? d, object? e,
        object? f, object? g, object? h, object? i, object? j) => false;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static System.Threading.Tasks.ValueTask<bool> ReturnValueTaskTrue_2Args(object? a, object? b)
        => new System.Threading.Tasks.ValueTask<bool>(true);
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static System.Threading.Tasks.ValueTask<bool> ReturnValueTaskTrue_3Args(object? a, object? b, object? c)
        => new System.Threading.Tasks.ValueTask<bool>(true);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static System.Threading.Tasks.ValueTask<System.Guid> ReturnValueTaskGuid_8Args(
        object? a, object? b, object? c, object? d, object? e, object? f, object? g, object? h)
        => new System.Threading.Tasks.ValueTask<System.Guid>(System.Guid.NewGuid());

    [MethodImpl(MethodImplOptions.NoInlining)] public static bool ReturnTrue_OneArg(object? a) => true;
    [MethodImpl(MethodImplOptions.NoInlining)] public static bool ReturnTrue_TwoArgs(object? a, object? b) => true;
    [MethodImpl(MethodImplOptions.NoInlining)] public static bool ReturnTrue_ThreeArgs(object? a, object? b, object? c) => true;

    [MethodImpl(MethodImplOptions.NoInlining)] public static string ReturnEmptyString_0Args() => string.Empty;
    [MethodImpl(MethodImplOptions.NoInlining)] public static string ReturnEmptyString_OneArg(object? a) => string.Empty;
    [MethodImpl(MethodImplOptions.NoInlining)] public static string ReturnEmptyString_TwoArgs(object? a, object? b) => string.Empty;

    [MethodImpl(MethodImplOptions.NoInlining)] public static int ReturnZero_0Args() => 0;

    // Runner identity sentinels for license-bound database getters. The headless
    // session has no License/Tenant; tests in 318-navtext-string-rewrite assert
    // that these surface a stable non-empty marker, so we return "STANDALONE"
    // (matching the runner's documented behavior for sandboxed environments).
    [MethodImpl(MethodImplOptions.NoInlining)] public static string ReturnStandalone_0Args() => "STANDALONE";

    // Zero-valued NavBigInteger for ALDatabase.ALLastUsedRowVersion /
    // ALMinimumActiveRowVersion. AL semantics: with no SQL backend, no rows have
    // ever been written, so the "last used" row version is the default (0).
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Microsoft.Dynamics.Nav.Runtime.NavBigInteger ReturnNavBigIntegerZero_0Args()
        => Microsoft.Dynamics.Nav.Runtime.NavBigInteger.Default;

    // ALNavApp.ALListResources — returns the package's bundled resource list.
    // The headless runner has no Diagnostics/AppMetadataRetriever, and the real
    // body itself returns an empty NavList<NavText> when metadata is missing.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Microsoft.Dynamics.Nav.Runtime.NavList<Microsoft.Dynamics.Nav.Runtime.NavText>
        ReturnEmptyNavTextList_2Args(object? a, object? b)
        => Microsoft.Dynamics.Nav.Runtime.NavList<Microsoft.Dynamics.Nav.Runtime.NavText>.Default;

    [MethodImpl(MethodImplOptions.NoInlining)] public static System.Threading.Tasks.ValueTask ReturnValueTask2(object? a, object? b) => default;
    [MethodImpl(MethodImplOptions.NoInlining)] public static System.Threading.Tasks.ValueTask ReturnValueTask3(object? a, object? b, object? c) => default;
    [MethodImpl(MethodImplOptions.NoInlining)] public static System.Threading.Tasks.ValueTask ReturnValueTask4(object? a, object? b, object? c, object? d) => default;
    [MethodImpl(MethodImplOptions.NoInlining)] public static System.Threading.Tasks.ValueTask ReturnValueTask5(object? a, object? b, object? c, object? d, object? e) => default;

    /// <summary>
    /// Replacement for DataProvider.TruncateAsync used by Record.TRUNCATE.
    /// Clears in-memory TempTableDataProvider rows and resets runner AutoIncrement
    /// counters when resetIdentity=true.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static System.Threading.Tasks.ValueTask DataProvider_TruncateAsync(
        object? self,
        int companyNameToken,
        Microsoft.Dynamics.Nav.Runtime.NCLMetaTable metaTable,
        object? filtersAndMarks,
        bool resetIdentity)
    {
        if (self == null)
            return default;

        var providerType = self.GetType();
        if (providerType.Name.Contains("TempTableDataProvider", StringComparison.Ordinal))
        {
            // Clear backing row tree(s).
            for (var t = providerType; t != null; t = t.BaseType)
            {
                foreach (var f in t.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (!f.Name.Contains("tree", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var value = f.GetValue(self);
                    if (value == null)
                        continue;
                    var clear = value.GetType().GetMethod("Clear",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null, Type.EmptyTypes, null);
                    clear?.Invoke(value, null);
                }
            }
        }

        if (resetIdentity)
        {
            var tableId = metaTable.TableId;
            if (tableId != 0)
                _aiCounters.TryRemove(tableId, out _);
            else
            {
                // Fallback if metadata table ID is unexpectedly unset.
                _fTruncateTtdpTable ??= providerType.GetField("table", BindingFlags.NonPublic | BindingFlags.Instance);
                var table = _fTruncateTtdpTable?.GetValue(self);
                var idObj = table?.GetType().GetProperty("TableId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(table);
                if (idObj is int fallbackId)
                    _aiCounters.TryRemove(fallbackId, out _);
            }
        }

        return default;
    }

    [MethodImpl(MethodImplOptions.NoInlining)] public static object? ReturnNull_OneArg(object a) => null;
    [MethodImpl(MethodImplOptions.NoInlining)] public static int ReturnZero_OneArg(object? a) => 0;

    // DataTransfer throw helpers — match BC's "DataTransfer is only usable during
    // upgrade and installation code." error so AL tests can `asserterror` + assert
    // the message. Signatures mirror the NoOpN family (receiver + N-1 args).
    private static System.Exception MakeDataTransferException()
    {
        var t = System.Type.GetType(
            "Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLDialogException, Microsoft.Dynamics.Nav.Types");
        const string msg = "DataTransfer is only usable during upgrade and installation code.";
        if (t != null)
        {
            var ctor = t.GetConstructor(new[] { typeof(string) });
            if (ctor != null) return (System.Exception)ctor.Invoke(new object[] { msg });
        }
        return new System.InvalidOperationException(msg);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void ThrowDataTransfer_OneArg(object? a) => throw MakeDataTransferException();
    [MethodImpl(MethodImplOptions.NoInlining)] public static void ThrowDataTransfer_2Args(object? a, object? b) => throw MakeDataTransferException();
    [MethodImpl(MethodImplOptions.NoInlining)] public static void ThrowDataTransfer_3Args(object? a, object? b, object? c) => throw MakeDataTransferException();
    [MethodImpl(MethodImplOptions.NoInlining)] public static void ThrowDataTransfer_4Args(object? a, object? b, object? c, object? d) => throw MakeDataTransferException();
    [MethodImpl(MethodImplOptions.NoInlining)] public static int ThrowDataTransferReturnInt_OneArg(object? a) => throw MakeDataTransferException();

    // TestPage field DrillDown() on a control with no OnDrillDown trigger — real BC (confirmed
    // 27.5 and 28.3, see al-language TestPageFieldDrillDown_Tests.FieldDrillDownWithNoTriggerIsRefused)
    // raises this exact fixed platform error regardless of TableRelation/UI state, so it is
    // reproducible faithfully in-process. Public (unlike MakeDataTransferException) because
    // RunnerPageInstance.RaiseOnDrillDown, in a different file/class, needs it too.
    internal static System.Exception MakeNavDrilldownActionNotSupportedException()
    {
        var t = System.Type.GetType(
            "Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLDialogException, Microsoft.Dynamics.Nav.Types");
        const string msg = "The NavDrilldownAction method is not supported.";
        if (t != null)
        {
            var ctor = t.GetConstructor(new[] { typeof(string) });
            if (ctor != null) return (System.Exception)ctor.Invoke(new object[] { msg });
        }
        return new System.InvalidOperationException(msg);
    }

    // Replacement for NavFile.GetTenantIds(NavSession session). The real body reads
    // session.Tenant.TenantSettings.AadTenantId / session.Tenant.Id — both null on the
    // headless runner skeleton. Faithful sentinel: empty AAD GUID + the same
    // "STANDALONE" tenant identifier used elsewhere by the runner (Database.TenantId,
    // Database.SerialNumber). Return type must be ValueTuple<Guid,string> exactly so
    // the calling convention (struct-return via hidden ptr) matches the original.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static System.ValueTuple<System.Guid, string> ReturnEmptyTenantIds_OneArg(object? a)
        => new System.ValueTuple<System.Guid, string>(System.Guid.Empty, "STANDALONE");

    /// <summary>
    /// Replacement for <c>NavNotification.ALSend(DataError)</c> / <c>ALRecall(DataError)</c>.
    ///
    /// BC's real body opens with <c>session.Diagnostics.SendTraceTag(...)</c>, which NREs on
    /// the skeleton session, so the body cannot simply be left alone the way ALConfirm and
    /// ALStrMenu now are. What it must NOT skip is the line just after that:
    ///     if (session.TestExecution.RedirectNotificationOperationToTestHandler(this, type))
    ///         return true;
    /// That IS the [SendNotificationHandler] / [RecallNotificationHandler] dispatch an AL test
    /// declares, and skipping it meant a declared handler never ran — the test then passed
    /// only because nothing noticed, which is what BC's own unexecuted-handler check exists to
    /// catch.
    ///
    /// When no handler is declared the redirect returns false and BC would go on to the real
    /// notification dispatch layer, which the runner has no equivalent for. BC's own answer
    /// inside a test run is to swallow it (<c>return executingTestRunner != null</c>), so that
    /// is what happens here: an unhandled notification is not an error, unlike an unhandled
    /// Message or Confirm. NotificationInfo.Id is populated first, mirroring the real body.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool NavNotification_ALSend(object self, object errorLevel)
        => RedirectNotification(self, "SendNotification");

    /// <summary>See <see cref="NavNotification_ALSend"/> — same contract, recall handler.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool NavNotification_ALRecall(object self, object errorLevel)
        => RedirectNotification(self, "RecallNotification");

    private static bool RedirectNotification(object self, string handlerTypeName)
    {
        PopulateNotificationId(self);
        try
        {
            var session = AlRunner.BcRuntime.SkeletonSession;
            var testExecution = session?.GetType()
                .GetProperty("TestExecution", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?
                .GetValue(session);
            var redirect = testExecution?.GetType().GetMethod("RedirectNotificationOperationToTestHandler",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (redirect == null) return true;

            var handlerType = Enum.Parse(redirect.GetParameters()[1].ParameterType, handlerTypeName);
            redirect.Invoke(testExecution, new[] { self, handlerType });
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            // An Error() inside the AL handler is the handler's own outcome, not runner noise.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
        }
        return true;
    }

    private static void PopulateNotificationId(object self)
    {
        try
        {
            var notifInfoProp = self.GetType().GetProperty("NotificationInfo",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var info = notifInfoProp?.GetValue(self);
            if (info == null) return;
            var idProp = info.GetType().GetProperty("Id");
            if (idProp == null || !idProp.CanRead || !idProp.CanWrite) return;
            var current = (System.Guid)(idProp.GetValue(info) ?? System.Guid.Empty);
            if (current == System.Guid.Empty)
                idProp.SetValue(info, System.Guid.NewGuid());
        }
        catch { /* best-effort Id population, exactly as the real body's mirror */ }
    }

    /// <summary>
    /// Replacement for <c>ALSystemOperatingSystem.GetUrlCore</c>. The real body reaches
    /// into <c>ALSession.ALCurrentClientType</c>, <c>NavEnvironment.Instance.Tenants</c>,
    /// and <c>NavCurrentThread.Session.Tenant.Id</c> — all of which NRE on the skeleton
    /// session. Returns a stub URL so AL tests that only verify non-empty result pass.
    /// Real URL generation is out of scope (requires a service-tier endpoint manager).
    /// NavClientType / NavObjectType are int-backed enums; declared here as int to match
    /// the native ABI slot layout JmpHook patches.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string ALSystemOperatingSystem_GetUrlCore(
        int clientType, string company, int objectType, int objectId,
        object record, bool useFilter, string layout)
        => "https://stub.example.com/";
    [MethodImpl(MethodImplOptions.NoInlining)] public static object? GetSkeletonCompanyReplacement(object self) => _skeletonCompany;

    /// <summary>
    /// Replacement for the static NCL <c>ALCompanyProperty.ALDisplayName()</c>. The real body
    /// reads from a NavRecord on table 2000000006 which the skeleton runtime can't serve
    /// (system-table DataAccess gap). Returns the stub display name "My Company" — observably
    /// equivalent to BC running with a Company row whose Display Name field is empty (the
    /// `GetCompanyDisplayNameDefaulted` fallback path). Faithful per docs/scope.md §2.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string ALCompanyProperty_ALDisplayName() => "My Company";

    /// <summary>
    /// Replacement for <c>NavSession.get_GlobalLanguage()</c>. Real body reads
    /// <c>cultureSettings.LCID</c>, but that struct field is zero-initialized on the
    /// GetUninitializedObject-built skeleton session. With IsOpen now seeded true, callers
    /// such as <c>RuntimeLanguage</c> read GlobalLanguage and pass it to
    /// <c>CultureInfo.GetCultureInfo(0)</c> which throws. Return 1033 (en-US) — the same
    /// value <c>NavEnvironment.DefaultLanguage</c> returns and what the pre-IsOpen-seeded code
    /// path was already using as fallback.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int NavSession_GlobalLanguage_1033(object self) => 1033;

    [MethodImpl(MethodImplOptions.NoInlining)] public static bool ReturnFalse_3Args(object? a, object? b, object? c) => false;
    [MethodImpl(MethodImplOptions.NoInlining)] public static bool ReturnFalse2(object? a, object? b) => false;

    /// <summary>
    /// Diagnostic helper used for the RecordImplementation.IsOpen hook — logs the call
    /// site so we can trace which patched receiver is being asked. Always returns true
    /// (record is open) because by the time the test harness asks, we want the read path
    /// to proceed against TempTableDataProvider rather than throw NotOpened.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ReturnTrue(object? a)
    {
        Console.Error.WriteLine($"[ReturnTrue] IsOpen hook fired for {a?.GetType().Name}");
        return true;
    }
}
