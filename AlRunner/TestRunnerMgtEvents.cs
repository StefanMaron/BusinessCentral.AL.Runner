using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner;

/// <summary>
/// Raises Microsoft's own <c>"Test Runner - Mgt"</c> (codeunit 130454, app "Test Runner") events
/// around each test codeunit and test method, in the order codeunit 130450 and the platform's
/// <c>NavTestExecution.BeforeTestRunAsync</c>/<c>AfterTestRunAsync</c> raise them, so Microsoft's
/// subscribers — 130453 "ALTestRunner Reset Environment" first of all — run as shipped. Nothing
/// here re-implements a subscriber (#4813). See docs/limitations.md#test-runner-events.
///
/// <para>Absent when codeunit 130454 is not loaded, or the loaded codeunit 130454 is not
/// Microsoft's "Test Runner - Mgt": then every call is a no-op, as on a tier without the app.
/// Microsoft's codeunit whose publishers cannot be bound is a <see cref="BcShapeGapException"/>,
/// never a silent no-op.</para>
/// </summary>
internal sealed class TestRunnerMgtEvents
{
    internal const int TestRunnerMgtId = 130454;
    internal const int ResetEnvironmentId = 130453;
    internal const int TestMethodLineTableId = 130450;
    internal const string TestRunnerMgtObjectName = "Test Runner - Mgt";

    // "Test Method Line" (table 130450) field numbers, from the shipped TestMethodLine.Table.al.
    private const int FieldLineType = 3, FieldTestCodeunit = 4, FieldName = 5, FieldFunction = 6,
        FieldRun = 7, FieldResult = 8;
    private const int LineTypeCodeunit = 0, LineTypeFunction = 1;
    private const int ResultFailure = 1, ResultSuccess = 2;

    private readonly MethodInfo _onBeforeCodeunitRun, _onAfterCodeunitRun,
        _onBeforeTestMethodRun, _onAfterTestMethodRun;
    private readonly Type _testPermissionsType;

    private TestRunnerMgtEvents(MethodInfo beforeCu, MethodInfo afterCu, MethodInfo beforeM, MethodInfo afterM)
    {
        _onBeforeCodeunitRun = beforeCu;
        _onAfterCodeunitRun = afterCu;
        _onBeforeTestMethodRun = beforeM;
        _onAfterTestMethodRun = afterM;
        _testPermissionsType = beforeM.GetParameters()[4].ParameterType;
    }

    /// <summary>
    /// The events of the loaded Microsoft "Test Runner - Mgt", or null when no such codeunit is
    /// loaded.
    /// </summary>
    internal static TestRunnerMgtEvents? Resolve()
    {
        var t = BcRuntime.FindCodeunitTypePublic(TestRunnerMgtId);
        if (t == null) return null;
        string? objectName;
        using (var handle = NewHandle(TestRunnerMgtId))
            objectName = handle.Target.ObjectName;
        // An app of the suite's own may number a codeunit 130454; that is not Microsoft's.
        if (!string.Equals(objectName, TestRunnerMgtObjectName, StringComparison.Ordinal))
            return null;
        return new TestRunnerMgtEvents(
            Bind(t, "OnBeforeCodeunitRun", 1),
            Bind(t, "OnAfterCodeunitRun", 1),
            Bind(t, "OnBeforeTestMethodRun", 6),
            Bind(t, "OnAfterTestMethodRun", 6));
    }

    private static MethodInfo Bind(Type t, string name, int arity)
    {
        var m = t.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(x => x.Name == name && x.GetParameters().Length == arity);
        return m ?? throw new AlRunner.Infrastructure.BcShapeGapException(
            $"Codeunit{TestRunnerMgtId} \"{TestRunnerMgtObjectName}\"", name,
            $"the loaded Test Runner app has no event publisher {name} with {arity} parameter(s), "
            + "so its per-test subscribers (130453 \"ALTestRunner Reset Environment\") cannot be raised.");
    }

    private NavDate? _workDateAtInitialize;

    /// <summary>
    /// <c>RunTests</c>' <c>ALTestRunnerResetEnvironment.Initialize()</c>: 130453 records the
    /// WorkDate its OnAfterCodeunitRun subscriber puts back. Called as each test codeunit starts,
    /// after the runner's codeunit-boundary reset.
    /// </summary>
    internal void InitializeResetEnvironment()
    {
        _workDateAtInitialize = Session()?.WorkDate;
        InvokeInitialize();
    }

    /// <summary>
    /// The runner's per-test reset (<c>--isolation test</c>) drops SingleInstance state, which BC
    /// never does, so 130453 would forget the WorkDate Initialize recorded. Re-run Initialize
    /// with the session's WorkDate set, for that call only, to the value first recorded — the
    /// state 130453 would still hold on BC.
    /// </summary>
    internal void ReinitializeAfterReset()
    {
        var session = Session();
        if (session == null || _workDateAtInitialize is not { } recorded)
        {
            InvokeInitialize();
            return;
        }
        var current = session.WorkDate;
        session.WorkDate = recorded;
        try { InvokeInitialize(); }
        finally { session.WorkDate = current; }
    }

    private static NavSession? Session() => BcRuntime.SkeletonSession as NavSession;

    private static void InvokeInitialize()
    {
        if (BcRuntime.FindCodeunitTypePublic(ResetEnvironmentId) == null) return;
        using var handle = NewHandle(ResetEnvironmentId);
        var target = handle.Target;
        var init = target.GetType().GetMethod("Initialize", BindingFlags.Instance | BindingFlags.Public,
            Type.EmptyTypes);
        if (init == null)
            throw new AlRunner.Infrastructure.BcShapeGapException(
                $"Codeunit{ResetEnvironmentId} \"ALTestRunner Reset Environment\"", "Initialize",
                "the loaded Test Runner app's 130453 has no parameterless Initialize procedure.");
        Invoke(init, target, Array.Empty<object?>());
    }

    internal void RaiseBeforeCodeunitRun(int codeunitId, string codeunitName) =>
        RaiseCodeunitEvent(_onBeforeCodeunitRun, codeunitId, codeunitName);

    internal void RaiseAfterCodeunitRun(int codeunitId, string codeunitName) =>
        RaiseCodeunitEvent(_onAfterCodeunitRun, codeunitId, codeunitName);

    private void RaiseCodeunitEvent(MethodInfo publisher, int codeunitId, string codeunitName)
    {
        using var mgtHandle = NewHandle(TestRunnerMgtId);
        var mgt = mgtHandle.Target;
        var line = NewLine(mgt, codeunitId, codeunitName, null, LineTypeCodeunit);
        Invoke(publisher, mgt, new object?[] { line });
    }

    /// <summary>
    /// OnBeforeTestMethodRun. Returns the subscribers' <c>Skip</c>: true means BC's
    /// <c>PlatformBeforeTestRun</c> answers false and the platform does not run the method.
    /// </summary>
    internal bool RaiseBeforeTestMethodRun(int codeunitId, string codeunitName, string functionName,
        object? functionTestPermissions)
    {
        using var mgtHandle = NewHandle(TestRunnerMgtId);
        var mgt = mgtHandle.Target;
        var line = NewLine(mgt, codeunitId, codeunitName, functionName, LineTypeFunction);
        var skip = false;
        var byRefType = _onBeforeTestMethodRun.GetParameters()[5].ParameterType;
        var byRef = CreateBoolByRef(byRefType, () => skip, v => skip = v);
        Invoke(_onBeforeTestMethodRun, mgt, new object?[]
        {
            line, codeunitId, new NavText(Truncate(codeunitName, 30)), new NavText(Truncate(functionName, 128)),
            TestPermissionsOrDefault(functionTestPermissions), byRef,
        });
        return skip;
    }

    internal void RaiseAfterTestMethodRun(int codeunitId, string codeunitName, string functionName,
        object? functionTestPermissions, bool isSuccess)
    {
        using var mgtHandle = NewHandle(TestRunnerMgtId);
        var mgt = mgtHandle.Target;
        var line = NewLine(mgt, codeunitId, codeunitName, functionName, LineTypeFunction);
        SetOption(line, FieldResult, isSuccess ? ResultSuccess : ResultFailure);
        Invoke(_onAfterTestMethodRun, mgt, new object?[]
        {
            line, codeunitId, new NavText(Truncate(codeunitName, 30)), new NavText(Truncate(functionName, 128)),
            TestPermissionsOrDefault(functionTestPermissions), isSuccess,
        });
    }

    // BC passes `testAttr?.TestPermissions ?? NavTestPermissions.Restrictive`
    // (NavTestExecution.BeforeTestRunAsync).
    private object TestPermissionsOrDefault(object? declared) =>
        declared != null && declared.GetType() == _testPermissionsType
            ? declared
            : Enum.Parse(_testPermissionsType, "Restrictive");

    // Disposed after each use: the handle parents on the process-wide root tree, and a
    // SingleInstance target (130453) stays alive through the runner's own keep-alive handle.
    private static NavCodeunitHandle NewHandle(int id) => new(BcRuntime.RootTreeStub!, id);

    // The line 130450 hands its subscribers carries the codeunit and function it is about; the
    // runner has no "Test Method Line" rows, so it is an uninserted record with those fields set.
    private static INavRecordHandle NewLine(NavCodeunit owner, int codeunitId, string codeunitName,
        string? functionName, int lineType)
    {
        var handle = new NavRecordHandle(owner, TestMethodLineTableId, false, (SecurityFiltering)0);
        var rec = handle.Target;
        SetOption(handle, FieldLineType, lineType);
        rec.SetFieldValueSafe(FieldTestCodeunit, NavType.Integer, ALCompiler.ToNavValue(codeunitId));
        rec.SetFieldValueSafe(FieldName, NavType.Text,
            new NavText(Truncate(functionName ?? codeunitName, 128)));
        rec.SetFieldValueSafe(FieldFunction, NavType.Text, new NavText(Truncate(functionName ?? "", 128)));
        rec.SetFieldValueSafe(FieldRun, NavType.Boolean, ALCompiler.ToNavValue(true));
        return handle;
    }

    private static void SetOption(INavRecordHandle handle, int fieldNo, int ordinal)
    {
        var rec = handle.Target;
        var current = (NavOption)rec.GetFieldValueSafe(fieldNo, NavType.Option);
        rec.SetFieldValueSafe(fieldNo, NavType.Option, current.CreateInstance(ordinal));
    }

    private static object CreateBoolByRef(Type byRefType, Func<bool> get, Action<bool> set)
    {
        // ByRef<bool>(GetValue<bool>, SetValue<bool>), as the compiled publisher's callers build it.
        var ctor = byRefType.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 2)
            ?? throw new AlRunner.Infrastructure.BcShapeGapException(
                byRefType.FullName ?? "ByRef<bool>", ".ctor",
                "no (getter, setter) constructor to pass OnBeforeTestMethodRun's var Skip.");
        var ps = ctor.GetParameters();
        var getter = Delegate.CreateDelegate(ps[0].ParameterType, get.Target, get.Method);
        var setter = Delegate.CreateDelegate(ps[1].ParameterType, set.Target, set.Method);
        return ctor.Invoke(new object[] { getter, setter });
    }

    private static void Invoke(MethodInfo m, object target, object?[] args)
    {
        try
        {
            BcRuntime.AwaitIfTask(m.Invoke(target, args));
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
