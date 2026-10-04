// HostEnvironmentGuardFixtures — INPUT for HostEnvironmentIsolationGuardTests (#5282), never run.
//
// Each type is one spelling of "mutate the host process environment" that the guard has to read
// out of the IL. Nothing calls these methods: the guard loads the type and reads its method
// bodies. The namespace is excluded from the assembly-wide sweep (which would otherwise refuse
// every one), and the guard pins that no test class hides in it.
//
// The variable named is one nothing reads. The two [ModuleInitializer] methods DO run at
// assembly load, so their calls sit behind `Armed`, which is false.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AlRunner.Tests.HostEnvironmentGuardFixtures;

internal static class FixtureState
{
    internal const string Variable = "AL_RUNNER_GUARD_FIXTURE_NEVER_READ";
    internal static bool Armed => false;
}

internal sealed class LiteralSetter
{
    internal void Run() => Environment.SetEnvironmentVariable("AL_RUNNER_GUARD_FIXTURE_NEVER_READ", "1");
}

internal sealed class ConstantSetter
{
    private const string Name = FixtureState.Variable;
    internal void Run() => Environment.SetEnvironmentVariable(Name, null);
}

internal sealed class ComputedNameSetter
{
    internal void Run(string suffix) => Environment.SetEnvironmentVariable($"AL_RUNNER_{suffix}", "1");
}

internal sealed class ThreeArgumentSetter
{
    internal void Run(string name) =>
        Environment.SetEnvironmentVariable(name, "1", EnvironmentVariableTarget.Process);
}

internal sealed class LambdaSetter
{
    internal Action Build(string name) => () => Environment.SetEnvironmentVariable(name, "1");
}

internal sealed class AsyncSetter
{
    internal async Task Run(string name)
    {
        await Task.Yield();
        Environment.SetEnvironmentVariable(name, "1");
    }
}

internal sealed class MethodGroupSetter
{
    internal Action<string, string?> Build() => Environment.SetEnvironmentVariable;
}

internal sealed class ExpressionSetter
{
    internal System.Linq.Expressions.Expression<Action<string, string?>> Build() =>
        (name, value) => Environment.SetEnvironmentVariable(name, value);
}

internal sealed class ReflectionSetter
{
    internal void Run(string name) =>
        typeof(Environment).GetMethod("SetEnvironmentVariable", new[] { typeof(string), typeof(string) })!
            .Invoke(null, new object?[] { name, "1" });
}

internal sealed class NativeSetter
{
    [DllImport("libc", EntryPoint = "setenv")]
    private static extern int Native(string name, string value, int overwrite);

    internal void Run(string name) => Native(name, "1", 1);
}

internal sealed class EnvironmentHelper
{
    internal static void Set(string name) => Environment.SetEnvironmentVariable(name, "1");
}

internal sealed class HelperCaller
{
    internal void Run() => EnvironmentHelper.Set("AL_RUNNER_GUARD_FIXTURE_NEVER_READ");
}

internal static class InitializerOnlySetter
{
    [ModuleInitializer]
    internal static void Init()
    {
        if (FixtureState.Armed) Environment.SetEnvironmentVariable(FixtureState.Variable, "1");
    }
}

internal static class MixedInitializerSetter
{
    [ModuleInitializer]
    internal static void Init()
    {
        if (FixtureState.Armed) Environment.SetEnvironmentVariable(FixtureState.Variable, "1");
    }

    internal static void Later(string name) => Environment.SetEnvironmentVariable(name, "1");
}

internal sealed class PureReader
{
    internal string? Run(string name) =>
        Environment.GetEnvironmentVariable(name) ?? Environment.CurrentDirectory;
}

internal sealed class ProseMentionsTheMethod
{
    internal string Run() => "Environment.SetEnvironmentVariable is process-wide, so this only reads";
}
