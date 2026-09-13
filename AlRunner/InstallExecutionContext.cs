using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Runtime.Apps;

namespace AlRunner;

/// <summary>
/// Sets BC's own <c>NavSession.AppInstallationContext</c> around one app's install triggers, the
/// way <c>NavAppInstallationProcessor.SetupSessionAndExecuteAction</c> does on a service tier:
/// <c>SetAppInstallationContext</c> before the triggers, <c>ClearAppInstallationContext</c> in a
/// finally. BC's readers of that field then answer from it (GetExecutionContext, DataTransfer,
/// StartSession, isolated events, PermissionSetBase); the list is in the PR for #4049.
/// Corpus codeunit 60589 <c>TestInstallExecCtx_*</c> pins what AL observes.
/// </summary>
internal static class InstallExecutionContext
{
    public static IDisposable Enter(Assembly appAssembly)
    {
        var session = NavCurrentThread.Session
            ?? throw new InvalidOperationException(
                "InstallExecutionContext: no NavCurrentThread.Session while firing install triggers; "
                + "cannot set NavSession.AppInstallationContext");

        var (appId, name, publisher, version) = BcRuntime.GetModuleAppInfoFor(appAssembly);
        var metadata = CreateRuntimeMetadata(appId, name, publisher, version);
        var group = (NavAppGroup)(BcRuntime.NavSession_NavAppGroup(session)
            ?? throw new InvalidOperationException("InstallExecutionContext: NavAppGroup.BaseGroup unresolved"));

        // hasData: false — the runner models a fresh install into an empty database, which is
        // what BC passes for an app with no prior data (ALNavApp.GetDataVersionForInstall answers
        // 0.0.0.0 from it; ObsolescenceGuard reads it too).
        var context = NavAppInstallationContext.CreateForInstall(
            session.Tenant, group, metadata, default!, activityId: string.Empty, hasData: false);

        session.SetAppInstallationContext(context);
        return new Scope(session);
    }

    private sealed class Scope(NavSession session) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                session.ClearAppInstallationContext();
        }
    }

    /// <summary>
    /// BC builds this from the published package. The runner has the manifest identity only, so
    /// every other constructor argument is its type's default. That is safe for the readers of
    /// the install context: ContainsObject answers false on a null lookup, and the trace in
    /// Set/ClearAppInstallationContext reads only ids, name, publisher and version.
    /// Trap: RuntimePackageId is a struct, so it reads as present (Guid.Empty) to
    /// ALNavApp.ALNavAppLoadPackageData — an install trigger calling NavApp.LoadPackageData
    /// reaches BC's package retriever instead of returning early.
    /// </summary>
    private static Microsoft.Dynamics.Nav.Apps.Runtime.NavAppRuntimeMetadata CreateRuntimeMetadata(Guid appId, string name, string publisher, string version)
    {
        var ctor = typeof(Microsoft.Dynamics.Nav.Apps.Runtime.NavAppRuntimeMetadata).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(c => c.GetParameters().Length)
            .First();
        var names = ctor.GetParameters().Select(p => p.Name).ToHashSet();
        foreach (var required in new[] { "appId", "name", "publisher", "version" })
            if (!names.Contains(required))
                throw new InvalidOperationException(
                    $"InstallExecutionContext: NavAppRuntimeMetadata constructor has no '{required}' parameter; "
                    + "the install context would name the wrong app (#4049)");
        var args = ctor.GetParameters().Select(p => p.Name switch
        {
            "appId" => ConvertGuid(p.ParameterType, appId),
            "name" => name,
            "publisher" => publisher,
            "version" => System.Version.TryParse(version, out var v) ? v : new System.Version(1, 0, 0, 0),
            _ => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null,
        }).ToArray();
        return (Microsoft.Dynamics.Nav.Apps.Runtime.NavAppRuntimeMetadata)ctor.Invoke(args);
    }

    private static object ConvertGuid(Type target, Guid value)
    {
        var implicitOp = target.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "op_Implicit" && m.ReturnType == target
                && m.GetParameters() is [{ ParameterType: var pt }] && pt == typeof(Guid));
        if (implicitOp != null) return implicitOp.Invoke(null, new object[] { value })!;
        var ctor = target.GetConstructor(new[] { typeof(Guid) })
            ?? throw new InvalidOperationException($"InstallExecutionContext: cannot build {target.FullName} from a Guid");
        return ctor.Invoke(new object[] { value });
    }
}
