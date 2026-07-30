// EnvironmentPatches — replacements for NavEnvironment statics that need to look like a
// real, initialised service-tier in headless mode (no host registration, no telemetry,
// no service-account principal).
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;

namespace AlRunner;

public static partial class BcRuntime
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavEnvironmentCctorReplacement()
    {
        var t = _navEnvironmentType!;
        FieldPoke.SetStatic(t, "lockObject", new object());
        FieldPoke.SetStatic(t, "instanceId", Guid.NewGuid());
        FieldPoke.SetStatic(t, "serviceInstanceName", string.Empty);
        FieldPoke.TryInitDefault(t, "compactLohGate");
        FieldPoke.TryInitDefault(t, "TerminatedSessionsMetric");
        FieldPoke.TryInitDefault(t, "defaultAwaitedShutdownConnectionTypesList");
        FieldPoke.TryInitDefault(t, "defaultRestartNotificationConnectionTypesList");

        // Topology is a static auto-prop the ctor reads at IL_0201 — a null backing field
        // NREs the very first non-trivial line of the real ctor. StandardServiceTopology
        // is the on-prem/standalone impl (matches the headless mode shape we want).
        var topoType = t.Assembly.GetType("Microsoft.Dynamics.Nav.Runtime.StandardServiceTopology");
        if (topoType != null)
        {
            var topo = Activator.CreateInstance(topoType);
            FieldPoke.SetStatic(t, "<Topology>k__BackingField", topo!);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object? GetServiceAccountReplacement() =>
        new System.Security.Principal.SecurityIdentifier("S-1-5-18");

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string GetServiceAccountNameReplacement() => "SYSTEM";

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ExecutionListenerCctorReplacement()
    {
        // Safely initialise the two static fields that the real cctor sets.
        // syncRoot is used for lock(syncRoot) in AddListener/RemoveListener.
        // Instance remains null — ALFunctionTimingExecutionListener.Start/Exit are
        // already no-op'd, so no caller will dereference Instance.
        var t = _navEnvironmentType!.Assembly
            .GetType("Microsoft.Dynamics.Nav.Runtime.ExecutionListener");
        if (t == null) return;
        FieldPoke.SetStatic(t, "syncRoot", new object());
        // Leave <Instance>k__BackingField null — safe because Start/Exit are no-op'd.
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object? GetInstanceReplacement()
    {
        var f = _navEnvironmentType!.GetField("instance", BindingFlags.NonPublic | BindingFlags.Static);
        return f?.GetValue(null);
    }
}
