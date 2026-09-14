using System.Reflection;
using System.Runtime.CompilerServices;

namespace AlRunner.Infrastructure;

/// <summary>
/// Installs an uninitialised stand-in into a BC singleton's static <c>instance</c> field
/// when the real factory could not build one (see <c>BcRuntime.ApplyAllPatches</c>).
/// </summary>
internal static class SkeletonFallback
{
    internal static void InstallOrThrow(Type type, FieldInfo instanceField, string hint)
    {
        try
        {
            // The allocation throws for a precise-init type and the static write for a
            // beforefieldinit one (NavEnvironment), so both stay inside the try.
            var skel = RuntimeHelpers.GetUninitializedObject(type);
            var instLock = type.GetField("lockObject", BindingFlags.NonPublic | BindingFlags.Instance);
            if (instLock != null) instLock.SetValue(skel, new object());
            instanceField.SetValue(null, skel);
        }
        catch (Exception e) when (AsInitializerFailure(e) is { } tie)
        {
            // Keyed on the install failing, not on matching tie.TypeName: the CLR reports a
            // nested type under its simple name, so a name match can miss and fall through
            // to the unexplained crash this exists to replace (#2064).
            var root = tie.InnerException ?? tie;
            throw new InvalidOperationException(
                $"{type.FullName}'s static constructor threw {root.GetType().FullName}: {root.Message} " +
                $"(type loaded from '{type.Assembly.Location}'). A type whose initializer failed cannot be " +
                $"used at all, so the runner cannot fall back to a skeleton instance. {hint}",
                tie);
        }
    }

    // Reflection's FieldInfo.SetValue wraps it in TargetInvocationException; the allocation does not.
    private static TypeInitializationException? AsInitializerFailure(Exception e)
        => e as TypeInitializationException ?? (e as TargetInvocationException)?.InnerException as TypeInitializationException;
}
